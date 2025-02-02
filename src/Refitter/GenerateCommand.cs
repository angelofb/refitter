using System.Diagnostics;
using System.Text.Json;

using Microsoft.OpenApi.Models;

using Refitter.Core;
using Refitter.Validation;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Refitter;

public sealed class GenerateCommand : AsyncCommand<Settings>
{
    private static readonly string Crlf = Environment.NewLine;

    public override ValidationResult Validate(CommandContext context, Settings settings)
    {
        if (!settings.NoLogging)
            Analytics.Configure();

        if (string.IsNullOrWhiteSpace(settings.OpenApiPath) &&
            string.IsNullOrWhiteSpace(settings.SettingsFilePath))
            return ValidationResult.Error("Input or settings file is required");

        if (!string.IsNullOrWhiteSpace(settings.OpenApiPath) &&
            !string.IsNullOrWhiteSpace(settings.SettingsFilePath))
            return ValidationResult.Error(
                "You should either specify an input URL/file directly " +
                "or use specify it in 'openApiPath' from the settings file, " +
                "not both");

        if (!string.IsNullOrWhiteSpace(settings.SettingsFilePath))
        {
            var json = File.ReadAllText(settings.SettingsFilePath);
            var refitGeneratorSettings = JsonSerializer.Deserialize<RefitGeneratorSettings>(json)!;
            settings.OpenApiPath = refitGeneratorSettings.OpenApiPath;
            
            if (string.IsNullOrWhiteSpace(refitGeneratorSettings.OpenApiPath))
                return ValidationResult.Error(
                    "The 'openApiPath' in settings file is required when " +
                    "URL or file path to OpenAPI Specification file " +
                    "is not specified in command line argument");
        }

        if (IsUrl(settings.OpenApiPath!))
            return base.Validate(context, settings);

        if (!string.IsNullOrWhiteSpace(settings.OperationNameTemplate) && !settings.OperationNameTemplate.Contains("{operationName}"))
            return ValidationResult.Error("'{operationName}' placeholder must be present in operation name template");

        return File.Exists(settings.OpenApiPath)
            ? base.Validate(context, settings)
            : ValidationResult.Error($"File not found - {Path.GetFullPath(settings.OpenApiPath!)}");
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var refitGeneratorSettings = new RefitGeneratorSettings
        {
            OpenApiPath = settings.OpenApiPath!,
            Namespace = settings.Namespace ?? "GeneratedCode",
            AddAutoGeneratedHeader = !settings.NoAutoGeneratedHeader,
            AddAcceptHeaders = !settings.NoAcceptHeaders,
            GenerateContracts = !settings.InterfaceOnly,
            ReturnIApiResponse = settings.ReturnIApiResponse,
            UseCancellationTokens = settings.UseCancellationTokens,
            GenerateOperationHeaders = !settings.NoOperationHeaders,
            UseIsoDateFormat = settings.UseIsoDateFormat,
            TypeAccessibility = settings.InternalTypeAccessibility
                ? TypeAccessibility.Internal
                : TypeAccessibility.Public,
            AdditionalNamespaces = settings.AdditionalNamespaces!,
            MultipleInterfaces = settings.MultipleInterfaces,
            IncludePathMatches = settings.MatchPaths ?? Array.Empty<string>(),
            IncludeTags = settings.Tags ?? Array.Empty<string>(),
            GenerateDeprecatedOperations = !settings.NoDeprecatedOperations,
            OperationNameTemplate = settings.OperationNameTemplate,
        };

        try
        {
            var stopwatch = Stopwatch.StartNew();
            AnsiConsole.MarkupLine($"[green]Refitter v{GetType().Assembly.GetName().Version!}[/]");
            AnsiConsole.MarkupLine(
                settings.NoLogging
                    ? "[green]Support key: Unavailable when logging is disabled[/]"
                    : $"[green]Support key: {SupportInformation.GetSupportKey()}[/]");

            if (!settings.SkipValidation)
                await ValidateOpenApiSpec(settings);

            if (!string.IsNullOrWhiteSpace(settings.SettingsFilePath))
            {
                var json = await File.ReadAllTextAsync(settings.SettingsFilePath);
                refitGeneratorSettings = JsonSerializer.Deserialize<RefitGeneratorSettings>(json)!;
                refitGeneratorSettings.OpenApiPath = settings.OpenApiPath!;
            }

            var generator = await RefitGenerator.CreateAsync(refitGeneratorSettings);
            var code = generator.Generate().ReplaceLineEndings();
            AnsiConsole.MarkupLine($"[green]Length: {code.Length} bytes[/]");

            if (!string.IsNullOrWhiteSpace(settings.OutputPath))
            {
                var directory = Path.GetDirectoryName(settings.OutputPath);
                if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);
            }

            var outputPath = settings.OutputPath ?? "Output.cs";
            AnsiConsole.MarkupLine($"[green]Output: {Path.GetFullPath(outputPath)}[/]");
            await File.WriteAllTextAsync(outputPath, code);
            await Analytics.LogFeatureUsage(settings);

            AnsiConsole.MarkupLine($"[green]Duration: {stopwatch.Elapsed}{Crlf}[/]");
            return 0;
        }
        catch (Exception exception)
        {
            if (exception is not OpenApiValidationException)
            {
                AnsiConsole.MarkupLine($"[red]Error:{Crlf}{exception.Message}[/]");
                AnsiConsole.MarkupLine($"[red]Exception:{Crlf}{exception.GetType()}[/]");
                AnsiConsole.MarkupLine($"[yellow]Stack Trace:{Crlf}{exception.StackTrace}[/]");
            }

            await Analytics.LogError(exception, settings);
            return exception.HResult;
        }
    }

    private static async Task ValidateOpenApiSpec(Settings settings)
    {
        var validationResult = await OpenApiValidator.Validate(settings.OpenApiPath!);
        if (!validationResult.IsValid)
        {
            AnsiConsole.MarkupLine($"[red]{Crlf}OpenAPI validation failed:{Crlf}[/]");

            foreach (var error in validationResult.Diagnostics.Errors)
            {
                TryWriteLine(error, "red", "Error");
            }

            foreach (var warning in validationResult.Diagnostics.Warnings)
            {
                TryWriteLine(warning, "yellow", "Warning");
            }

            validationResult.ThrowIfInvalid();
        }

        AnsiConsole.MarkupLine($"[green]{Crlf}OpenAPI statistics:{Crlf}{validationResult.Statistics}{Crlf}[/]");
    }

    private static void TryWriteLine(
        OpenApiError error,
        string color,
        string label)
    {
        try
        {
            AnsiConsole.MarkupLine($"[{color}]{label}:{Crlf}{error}{Crlf}[/]");
        }
        catch
        {
            // ignored
        }
    }

    private static bool IsUrl(string openApiPath)
    {
        return Uri.TryCreate(openApiPath, UriKind.Absolute, out var uriResult) &&
               (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
    }
}