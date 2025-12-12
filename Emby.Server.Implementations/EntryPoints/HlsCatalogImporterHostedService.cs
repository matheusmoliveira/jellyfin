using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Extensions;
using MediaBrowser.Controller;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.EntryPoints;

/// <summary>
/// Reads the hls-catalog.json file and ensures the library contains matching virtual items.
/// </summary>
public sealed class HlsCatalogImporterHostedService : IHostedService
{
    private const string CatalogFileName = "hls-catalog.json";
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions CatalogSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<HlsCatalogImporterHostedService> _logger;
    private readonly IServerApplicationPaths _appPaths;
    private readonly ILibraryManager _libraryManager;
    private CancellationTokenSource? _cts;
    private Task? _backgroundTask;
    private readonly TimeSpan _pollInterval;
    private readonly string _moviesBaseUrl;
    private readonly string _seriesBaseUrl;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsCatalogImporterHostedService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="appPaths">Application path provider.</param>
    /// <param name="libraryManager">Library manager for triggering scans.</param>
    public HlsCatalogImporterHostedService(
        ILogger<HlsCatalogImporterHostedService> logger,
        IServerApplicationPaths appPaths,
        ILibraryManager libraryManager)
    {
        _logger = logger;
        _appPaths = appPaths;
        _libraryManager = libraryManager;

        // Configuráveis via env:
        // - JELLYFIN_HLS_CATALOG_POLL_SECONDS (default: 120)
        // - JELLYFIN_HLS_CATALOG_MOVIES_BASE_URL (default: https://cdn.codexsengineer.com.br/filmes)
        // - JELLYFIN_HLS_CATALOG_SERIES_BASE_URL (default: https://cdn.codexsengineer.com.br/series)
        if (int.TryParse(Environment.GetEnvironmentVariable("JELLYFIN_HLS_CATALOG_POLL_SECONDS"), out var pollSeconds)
            && pollSeconds > 0)
        {
            _pollInterval = TimeSpan.FromSeconds(pollSeconds);
        }
        else
        {
            _pollInterval = DefaultPollInterval;
        }

        _moviesBaseUrl = (Environment.GetEnvironmentVariable("JELLYFIN_HLS_CATALOG_MOVIES_BASE_URL")
                          ?? "https://cdn.codexsengineer.com.br/filmes").TrimEnd('/');
        _seriesBaseUrl = (Environment.GetEnvironmentVariable("JELLYFIN_HLS_CATALOG_SERIES_BASE_URL")
                          ?? "https://cdn.codexsengineer.com.br/series").TrimEnd('/');
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _backgroundTask = Task.Run(() => RunLoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null)
        {
            return;
        }

        try
        {
            _cts.Cancel();
        }
        catch
        {
            // ignore
        }

        if (_backgroundTask is not null)
        {
            try
            {
                await _backgroundTask.ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        var catalogPath = Path.Combine(_appPaths.DataPath, CatalogFileName);
        DateTimeOffset? lastWrite = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(catalogPath))
                {
                    var currentWrite = File.GetLastWriteTimeUtc(catalogPath);
                    var currentWriteOffset = new DateTimeOffset(currentWrite, TimeSpan.Zero);

                    if (lastWrite is null || currentWriteOffset > lastWrite.Value)
                    {
                        _logger.LogInformation("HLS catalog changed (or first run). Syncing from '{Catalog}'.", catalogPath);
                        await SyncCatalogAsync(cancellationToken).ConfigureAwait(false);
                        lastWrite = currentWriteOffset;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error while watching HLS catalog.");
            }

            try
            {
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task SyncCatalogAsync(CancellationToken cancellationToken)
    {
        var catalogPath = Path.Combine(_appPaths.DataPath, CatalogFileName);
        if (!File.Exists(catalogPath))
        {
            _logger.LogInformation("HLS catalog '{Catalog}' not found. Skipping catalog import.", catalogPath);
            return;
        }

        HlsCatalogEntry[]? entries;
        try
        {
            await using var stream = File.OpenRead(catalogPath);
            entries = await JsonSerializer.DeserializeAsync<HlsCatalogEntry[]>(stream, CatalogSerializerOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read HLS catalog '{Catalog}'.", catalogPath);
            return;
        }

        if (entries is null || entries.Length == 0)
        {
            _logger.LogInformation("HLS catalog '{Catalog}' is empty.", catalogPath);
            return;
        }

        var filesCreated = 0;
        foreach (var entry in entries)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var created = await UpsertEntryAsync(entry, cancellationToken).ConfigureAwait(false);
                if (created)
                {
                    filesCreated++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to import catalog entry {Title}.", entry.Title);
            }
        }

        // Se criamos ou atualizamos arquivos .strm, força um scan da biblioteca
        if (filesCreated > 0)
        {
            _logger.LogInformation("Created/updated {Count} .strm file(s). Triggering library scan...", filesCreated);
            try
            {
                await _libraryManager.ValidateMediaLibrary(new Progress<double>(), cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Library scan completed. New .strm files should now be indexed.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to trigger library scan after creating .strm files.");
            }
        }
    }

    private async Task<bool> UpsertEntryAsync(HlsCatalogEntry entry, CancellationToken cancellationToken)
    {
        // Tenta extrair IMDb ID do slug se não foi fornecido explicitamente
        if (string.IsNullOrWhiteSpace(entry.ImdbId) && !string.IsNullOrWhiteSpace(entry.Slug))
        {
            var extractedImdbId = ExtractImdbIdFromSlug(entry.Slug);
            if (!string.IsNullOrWhiteSpace(extractedImdbId))
            {
                entry.ImdbId = extractedImdbId;
                _logger.LogDebug("Extracted IMDb ID '{ImdbId}' from slug '{Slug}'.", extractedImdbId, entry.Slug);
            }
        }

        if (string.IsNullOrWhiteSpace(entry.LibraryPath))
        {
            _logger.LogWarning("Skipping catalog entry due to missing LibraryPath. Title='{Title}'.", entry.Title ?? "??");
            return false;
        }

        // Verifica se o caminho da biblioteca existe
        if (!Directory.Exists(entry.LibraryPath))
        {
            _logger.LogWarning("Library path '{Path}' does not exist. Skipping entry '{Title}'.", entry.LibraryPath, entry.Title ?? "??");
            return false;
        }

        // Gera o nome do arquivo .strm baseado no slug ou título
        var slug = GetSlug(entry);
        string strmPath;

        // Detecta se é série pelo slug
        var isSeries = !string.IsNullOrWhiteSpace(slug) && IsSeriesSlug(slug);

        if (isSeries && !string.IsNullOrWhiteSpace(slug))
        {
            // Para séries, cria a estrutura de pastas: Series Name (Year)\Season XX\Episode.strm
            var seriesInfo = ExtractSeriesInfoFromSlug(slug, entry);
            if (seriesInfo != null)
            {
                // Cria o caminho: libraryPath\Series Name (Year)\Season XX\Series Name SXXEXX.strm
                var seriesFolder = $"{seriesInfo.SeriesName} ({seriesInfo.Year})";
                var seasonFolder = $"Season {seriesInfo.SeasonNumber.PadLeft(2, '0')}";
                var episodeFileName = $"{seriesInfo.SeriesName} S{seriesInfo.SeasonNumber.PadLeft(2, '0')}E{seriesInfo.EpisodeNumber.PadLeft(2, '0')}";

                var seriesPath = Path.Combine(entry.LibraryPath, seriesFolder, seasonFolder);
                strmPath = Path.Combine(seriesPath, $"{episodeFileName}.strm");
            }
            else
            {
                // Fallback: usa o slug como nome do arquivo
                var fileName = SanitizeFileName(slug ?? entry.Title ?? Guid.NewGuid().ToString());
                strmPath = Path.Combine(entry.LibraryPath, $"{fileName}.strm");
            }
        }
        else
        {
            // Para filmes, cria diretamente na pasta da biblioteca
            var fileName = SanitizeFileName(slug ?? entry.Title ?? Guid.NewGuid().ToString());
            strmPath = Path.Combine(entry.LibraryPath, $"{fileName}.strm");
        }

        // Constrói a URL do stream HLS externo
        // Se não tiver ExternalUrl, usa o padrão baseado no slug
        string streamUrl;
        if (!string.IsNullOrWhiteSpace(entry.ExternalUrl))
        {
            streamUrl = entry.ExternalUrl;
        }
        else if (!string.IsNullOrWhiteSpace(slug))
        {
            // Reutiliza a detecção de série já feita acima
            if (isSeries)
            {
                // Para séries, extrai o nome da série e temporada do slug
                // Exemplo: "stranger-things-s05e01-imdbid-tt4574334" -> "stranger-things-2016/season-05"
                var seriesPath = ExtractSeriesPathFromSlug(slug);
                if (!string.IsNullOrWhiteSpace(seriesPath))
                {
                    streamUrl = $"{_seriesBaseUrl}/{EncodeUrlPath(seriesPath)}/hls/master.m3u8";
                }
                else
                {
                    // Fallback: usa o slug completo
                    streamUrl = $"{_seriesBaseUrl}/{EncodeUrlPath(slug)}/hls/master.m3u8";
                }
            }
            else
            {
                // Padrão para filmes: {MOVIES_BASE_URL}/{slug}/hls/master.m3u8
                streamUrl = $"{_moviesBaseUrl}/{EncodeUrlPath(slug)}/hls/master.m3u8";
            }
        }
        else
        {
            _logger.LogWarning("Cannot generate stream URL for entry '{Title}'. Missing slug or ExternalUrl.", entry.Title ?? "??");
            return false;
        }

        // Verifica se o arquivo .strm já existe e se a URL mudou
        var fileExists = File.Exists(strmPath);
        if (fileExists)
        {
            var existingContent = await File.ReadAllTextAsync(strmPath, cancellationToken).ConfigureAwait(false);
            if (existingContent.Trim() == streamUrl)
            {
                _logger.LogDebug("File '{Path}' already exists with correct URL. Skipping.", strmPath);
                return false; // Arquivo já existe e está correto, não precisa de scan
            }

            _logger.LogDebug("Updating existing .strm file '{Path}' with new URL.", strmPath);
        }

        // Cria o diretório se não existir
        var directory = Path.GetDirectoryName(strmPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Escreve o arquivo .strm com a URL do stream
        await File.WriteAllTextAsync(strmPath, streamUrl, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Created/updated .strm file '{Path}' for '{Title}' pointing to '{Url}'. Jellyfin will automatically detect and index this file.", strmPath, entry.Title ?? "??", streamUrl);
        return true; // Arquivo foi criado ou atualizado, precisa de scan
    }

    private static string? GetSlug(HlsCatalogEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Slug))
        {
            return entry.Slug;
        }

        var normalized = entry.Title!.Normalize(NormalizationForm.FormD);
        var chars = normalized
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Select(char.ToLowerInvariant)
            .ToArray();

        var slug = new string(chars);
        slug = System.Text.RegularExpressions.Regex.Replace(slug, @"[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? null : $"{slug}-imdbid-{entry.ImdbId!.ToLowerInvariant()}";
    }

    private static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "virtual-item" : cleaned;
    }

    /// <summary>
    /// Extracts IMDb ID from a slug following the pattern: nome-imdbid-ttXXXXXXX.
    /// </summary>
    /// <param name="slug">The slug to extract from.</param>
    /// <returns>The IMDb ID (ttXXXXXXX) if found, null otherwise.</returns>
    private static string? ExtractImdbIdFromSlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        // Pattern: anything-imdbid-ttXXXXXXX
        // Example: "60-segundos-imdbid-tt0187078" -> "tt0187078"
        var match = System.Text.RegularExpressions.Regex.Match(
            slug,
            @"-imdbid-(tt\d{7,8})(?:-|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (match.Success && match.Groups.Count > 1)
        {
            return match.Groups[1].Value;
        }

        return null;
    }

    /// <summary>
    /// Extracts series information (name, year, season, episode) from a slug and entry.
    /// </summary>
    /// <param name="slug">The slug to extract from.</param>
    /// <param name="entry">The catalog entry (for title and year if available).</param>
    /// <returns>SeriesInfo if extraction succeeds, null otherwise.</returns>
    private static SeriesInfo? ExtractSeriesInfoFromSlug(string slug, HlsCatalogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        var info = new SeriesInfo();

        // Pattern 1: "series-name-sXXeXX-imdbid-ttXXXXXXX"
        // Example: "stranger-things-s05e01-imdbid-tt4574334"
        var match = Regex.Match(
            slug,
            @"^(.+?)-s(\d+)e(\d+)(?:-imdbid-tt\d{7,8})?$",
            RegexOptions.IgnoreCase);

        if (match.Success && match.Groups.Count >= 4)
        {
            var seriesNameSlug = match.Groups[1].Value;
            info.SeasonNumber = match.Groups[2].Value;
            info.EpisodeNumber = match.Groups[3].Value;

            // Tenta extrair o ano do nome da série se houver (ex: "stranger-things-2016")
            var yearMatch = Regex.Match(seriesNameSlug, @"-(\d{4})$");
            if (yearMatch.Success)
            {
                info.Year = yearMatch.Groups[1].Value;
                // Remove o ano do nome
                seriesNameSlug = seriesNameSlug.Substring(0, yearMatch.Index);
            }
            else if (entry.Year.HasValue)
            {
                // Usa o ano do entry se disponível
                info.Year = entry.Year.Value.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                // Se não tiver ano, usa o ano atual como fallback (pode ser ajustado depois)
                info.Year = DateTime.Now.Year.ToString(CultureInfo.InvariantCulture);
            }

            // Converte o slug do nome da série para um nome legível
            // Ex: "stranger-things" -> "Stranger Things"
            info.SeriesName = ConvertSlugToTitle(seriesNameSlug);

            return info;
        }

        // Pattern 2: "series-name-season-XX-episode-XX-imdbid-ttXXXXXXX"
        match = Regex.Match(
            slug,
            @"^(.+?)-season-(\d+)(?:-episode-(\d+))?(?:-imdbid-tt\d{7,8})?$",
            RegexOptions.IgnoreCase);

        if (match.Success && match.Groups.Count >= 3)
        {
            var seriesNameSlug = match.Groups[1].Value;
            info.SeasonNumber = match.Groups[2].Value;
            info.EpisodeNumber = match.Groups.Count > 3 && !string.IsNullOrWhiteSpace(match.Groups[3].Value)
                ? match.Groups[3].Value
                : "01"; // Default para episódio 01 se não especificado

            // Tenta extrair o ano
            var yearMatch = Regex.Match(seriesNameSlug, @"-(\d{4})$");
            if (yearMatch.Success)
            {
                info.Year = yearMatch.Groups[1].Value;
                seriesNameSlug = seriesNameSlug.Substring(0, yearMatch.Index);
            }
            else if (entry.Year.HasValue)
            {
                info.Year = entry.Year.Value.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                info.Year = DateTime.Now.Year.ToString(CultureInfo.InvariantCulture);
            }

            info.SeriesName = ConvertSlugToTitle(seriesNameSlug);
            return info;
        }

        // Se não conseguir extrair, retorna null
        return null;
    }

    /// <summary>
    /// Converts a slug to a readable title (e.g., "stranger-things" -> "Stranger Things").
    /// </summary>
    /// <param name="slug">The slug to convert.</param>
    /// <returns>The converted title.</returns>
    private static string ConvertSlugToTitle(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return "Unknown Series";
        }

        // Remove hífens e capitaliza cada palavra
        var words = slug.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var title = string.Join(" ", words.Select(word =>
        {
            if (word.Length == 0)
            {
                return word;
            }

            return char.ToUpperInvariant(word[0]) + (word.Length > 1 ? word.Substring(1) : string.Empty);
        }));

        return string.IsNullOrWhiteSpace(title) ? "Unknown Series" : title;
    }

    /// <summary>
    /// Determines if a slug represents a series (TV show) based on common patterns.
    /// </summary>
    /// <param name="slug">The slug to check.</param>
    /// <returns>True if the slug appears to be a series, false otherwise.</returns>
    private static bool IsSeriesSlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return false;
        }

        // Patterns that indicate a series:
        // - Contains "season" (case insensitive)
        // - Contains "sXXeXX" pattern (e.g., "s05e01", "s1e1")
        // - Contains "episode" (case insensitive)
        return Regex.IsMatch(slug, @"(?:season|s\d+e\d+|episode)", RegexOptions.IgnoreCase);
    }

    private static string EncodeUrlPath(string path)
    {
        // Mantém separadores '/' e faz escape de cada segmento (espaços, parênteses, etc).
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join("/", parts.Select(Uri.EscapeDataString));
    }

    /// <summary>
    /// Extracts the series path from a slug for series entries.
    /// Example: "stranger-things-s05e01-imdbid-tt4574334" -> "stranger-things-2016/season-05".
    /// </summary>
    /// <param name="slug">The slug to extract from.</param>
    /// <returns>The series path (series-name-year/season-XX) if found, null otherwise.</returns>
    private static string? ExtractSeriesPathFromSlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        // Pattern 1: "series-name-sXXeXX-imdbid-ttXXXXXXX"
        // Example: "stranger-things-s05e01-imdbid-tt4574334"
        // Extract: series name and season number
        var match = Regex.Match(
            slug,
            @"^(.+?)-s(\d+)e\d+(?:-imdbid-tt\d{7,8})?$",
            RegexOptions.IgnoreCase);

        if (match.Success && match.Groups.Count >= 3)
        {
            var seriesName = match.Groups[1].Value;
            var seasonNumber = match.Groups[2].Value;

            // Tenta extrair o ano do nome da série se houver (ex: "stranger-things-2016")
            var yearMatch = Regex.Match(seriesName, @"-(\d{4})$");
            if (yearMatch.Success)
            {
                // Já tem o ano no nome
                return $"{seriesName}/season-{seasonNumber.PadLeft(2, '0')}";
            }

            // Se não tiver ano, retorna apenas o nome e temporada
            // O usuário pode adicionar o ano manualmente no slug se necessário
            return $"{seriesName}/season-{seasonNumber.PadLeft(2, '0')}";
        }

        // Pattern 2: "series-name-season-XX-imdbid-ttXXXXXXX"
        // Example: "stranger-things-season-05-imdbid-tt4574334"
        match = Regex.Match(
            slug,
            @"^(.+?)-season-(\d+)(?:-imdbid-tt\d{7,8})?$",
            RegexOptions.IgnoreCase);

        if (match.Success && match.Groups.Count >= 3)
        {
            var seriesName = match.Groups[1].Value;
            var seasonNumber = match.Groups[2].Value;
            return $"{seriesName}/season-{seasonNumber.PadLeft(2, '0')}";
        }

        // Se não conseguir extrair, retorna null para usar fallback
        return null;
    }

    /// <summary>
    /// Represents extracted series information from a slug.
    /// </summary>
    private sealed class SeriesInfo
    {
        public string SeriesName { get; set; } = string.Empty;

        public string Year { get; set; } = string.Empty;

        public string SeasonNumber { get; set; } = string.Empty;

        public string EpisodeNumber { get; set; } = string.Empty;
    }
}
