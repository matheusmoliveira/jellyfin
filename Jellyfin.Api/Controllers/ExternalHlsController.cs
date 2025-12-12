using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Controllers
{
    /// <summary>
    /// Controller for serving external HLS files (master.m3u8) via HTTP proxy.
    /// Routes: /HlsExternal/{folderKey}/master.m3u8.
    /// </summary>
    [ApiController]
    [Route("HlsExternal")]
    public class ExternalHlsController : BaseJellyfinApiController
    {
        // Configuração via variáveis de ambiente (ideal para Docker/Deploy simples):
        // - JELLYFIN_EXTERNAL_HLS_ORIGIN_BASE_URL: base do origin (onde os arquivos realmente estão), ex:
        //     https://origin.codexsengineer.com.br/filmes
        // - JELLYFIN_EXTERNAL_HLS_PUBLIC_BASE_URL: base pública (CDN), ex:
        //     https://cdn.codexsengineer.com.br/filmes
        //
        // Observação: ambas devem apontar para o mesmo "prefixo" (/filmes), pois as URLs são reescritas
        // para /{folderKey}/hls/{file}.
        private readonly string _originBaseUrl;
        private readonly string _publicBaseUrl;

        private readonly ILibraryManager _libraryManager;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<ExternalHlsController> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="ExternalHlsController"/> class.
        /// </summary>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="httpClientFactory">HTTP client factory.</param>
        /// <param name="logger">The logger.</param>
        public ExternalHlsController(
            ILibraryManager libraryManager,
            IHttpClientFactory httpClientFactory,
            ILogger<ExternalHlsController> logger)
        {
            _libraryManager = libraryManager;
            _httpClientFactory = httpClientFactory;
            _logger = logger;

            // Defaults: mantém compatibilidade com o comportamento antigo caso nada seja configurado.
            _originBaseUrl = (Environment.GetEnvironmentVariable("JELLYFIN_EXTERNAL_HLS_ORIGIN_BASE_URL")
                              ?? "https://origin.codexsengineer.com.br/filmes").TrimEnd('/');
            _publicBaseUrl = (Environment.GetEnvironmentVariable("JELLYFIN_EXTERNAL_HLS_PUBLIC_BASE_URL")
                              ?? "https://cdn.codexsengineer.com.br/filmes").TrimEnd('/');
        }

        /// <summary>
        /// Serves an external HLS master playlist or segment file by proxying the request.
        /// </summary>
        /// <param name="folderKey">The folder key (e.g., "nome-imdbid-tt1234567").</param>
        /// <param name="file">The file path relative to the HLS directory (e.g., "master.m3u8" or "1080p/segment001.ts").</param>
        /// <returns>The HLS file content.</returns>
        [HttpGet("{folderKey}/{*file}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult> GetExternalHlsFile([FromRoute] string folderKey, [FromRoute] string file)
        {
            if (string.IsNullOrEmpty(folderKey) || string.IsNullOrEmpty(file))
            {
                return BadRequest("IMDb ID and file path are required");
            }

            // Build the external URL
            // Pattern: {ORIGIN_BASE}/{folderKey}/hls/{file}
            var cleanFile = file.TrimStart('/');
            var externalUrl = $"{_originBaseUrl}/{folderKey}/hls/{cleanFile}";

            _logger.LogInformation("Proxying external HLS request: {Url}", externalUrl);

            try
            {
                var httpClient = _httpClientFactory.CreateClient(NamedClient.Default);

                var response = await httpClient.GetAsync(externalUrl, HttpContext.RequestAborted).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to fetch external HLS file: {Url}, Status: {Status}", externalUrl, response.StatusCode);
                    return StatusCode((int)response.StatusCode);
                }

                var content = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

                // Determine content type
                var extension = Path.GetExtension(file).ToLowerInvariant();
                var contentType = extension switch
                {
                    ".m3u8" => "application/x-mpegURL",
                    ".ts" => "video/mp2t",
                    ".mp4" => "video/mp4",
                    ".m4s" => "video/mp4",
                    _ => "application/octet-stream"
                };

                // If it's a master.m3u8 or variant playlist file, rewrite URLs to point to our controller
                if (extension == ".m3u8")
                {
                    var masterContent = System.Text.Encoding.UTF8.GetString(content);

                    // Valida o conteúdo antes de processar
                    if (string.IsNullOrWhiteSpace(masterContent))
                    {
                        _logger.LogWarning("Conteúdo do arquivo .m3u8 está vazio para {File}", file);
                        return Content(masterContent, contentType);
                    }

                    // Log das primeiras linhas para debug
                    var firstLines = masterContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None)
                        .Take(20)
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .ToList();
                    _logger.LogInformation("Primeiras linhas do .m3u8 para {FolderKey}/{File}: {Lines}", folderKey, file, string.Join(" | ", firstLines));

                    // Log de todas as linhas que contêm URLs para debug
                    var urlLines = masterContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None)
                        .Where(l => !string.IsNullOrWhiteSpace(l) &&
                                    !l.TrimStart().StartsWith('#') &&
                                    (l.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) || l.Contains(".ts", StringComparison.OrdinalIgnoreCase) || l.Contains(".mp4", StringComparison.OrdinalIgnoreCase) || l.Contains(".m4s", StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                    _logger.LogInformation("Linhas com URLs encontradas no .m3u8 para {FolderKey}/{File}: {Count} linhas", folderKey, file, urlLines.Count);
                    foreach (var urlLine in urlLines.Take(10))
                    {
                        _logger.LogInformation("URL encontrada: {Url}", urlLine.Trim());
                    }

                    // Rewrite relative URLs to point to our controller
                    // Match lines that are not comments and contain file extensions
                    var directory = string.Empty;
                    var lastSlash = file.LastIndexOf('/');
                    if (lastSlash >= 0)
                    {
                        directory = file[..lastSlash];
                    }

                    // Reescreve para a base pública (CDN) em vez de apontar de volta para o Jellyfin.
                    // Ex.: https://cdn.codexsengineer.com.br/filmes/{folderKey}/hls/...
                    var baseUrl = $"{_publicBaseUrl}/{folderKey}/hls";
                    if (!string.IsNullOrEmpty(directory))
                    {
                        baseUrl = $"{baseUrl}/{directory.TrimStart('/')}";
                    }

                    _logger.LogDebug("Processando arquivo .m3u8: {File}, BaseUrl: {BaseUrl}, Directory: {Directory}", file, baseUrl, directory);

                    // Processa linha por linha para melhor controle e validação
                    var lines = masterContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                    var rewrittenLines = new List<string>();

                    foreach (var line in lines)
                    {
                        // Valida a linha antes de processar
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            rewrittenLines.Add(line);
                            continue;
                        }

                        // Se é um comentário, mantém como está
                        var trimmedStart = line.TrimStart();
                        if (trimmedStart.Length > 0 && trimmedStart[0] == '#')
                        {
                            rewrittenLines.Add(line);
                            continue;
                        }

                        // Verifica se a linha contém uma extensão de arquivo de mídia
                        var trimmedLine = line.Trim();
                        if (string.IsNullOrWhiteSpace(trimmedLine))
                        {
                            rewrittenLines.Add(line);
                            continue;
                        }

                        // Verifica se é um arquivo de mídia (termina com extensão conhecida)
                        var isMediaFile = trimmedLine.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                                        trimmedLine.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
                                        trimmedLine.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                                        trimmedLine.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase);

                        if (isMediaFile)
                        {
                            // Se já é uma URL absoluta, mantém como está
                            if (Uri.TryCreate(trimmedLine, UriKind.Absolute, out _))
                            {
                                rewrittenLines.Add(line);
                                continue;
                            }

                            // Valida o baseUrl antes de usar
                            if (string.IsNullOrWhiteSpace(baseUrl))
                            {
                                _logger.LogWarning("BaseUrl vazio ao reescrever URL HLS. Mantendo linha original: {Line}", trimmedLine);
                                rewrittenLines.Add(line);
                                continue;
                            }

                            // Remove barras duplicadas e espaços - usando null-conditional para segurança
                            var cleanPath = trimmedLine?.TrimStart('/')?.Trim();
                            if (string.IsNullOrWhiteSpace(cleanPath))
                            {
                                _logger.LogWarning("Caminho limpo está vazio ou nulo. Mantendo linha original: {Line}", trimmedLine ?? "(null)");
                                rewrittenLines.Add(line);
                                continue;
                            }

                            // Valida que baseUrl não é null antes de usar
                            var safeBaseUrl = baseUrl?.TrimEnd('/');
                            if (string.IsNullOrWhiteSpace(safeBaseUrl))
                            {
                                _logger.LogWarning("BaseUrl inválido após trim. Mantendo linha original: {Line}", trimmedLine);
                                rewrittenLines.Add(line);
                                continue;
                            }

                            // Constrói a URL reescrita
                            var rewrittenUrl = $"{safeBaseUrl}/{cleanPath}";
                            _logger.LogDebug("Reescrevendo URL HLS: {Original} -> {Rewritten}", trimmedLine, rewrittenUrl);
                            rewrittenLines.Add(rewrittenUrl);
                        }
                        else
                        {
                            // Mantém linhas que não são arquivos de mídia como estão
                            rewrittenLines.Add(line);
                        }
                    }

                    var rewrittenContent = string.Join("\n", rewrittenLines);
                    return Content(rewrittenContent, contentType);
                }

                return File(content, contentType, enableRangeProcessing: true);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Error fetching external HLS file: {Url}", externalUrl);
                return StatusCode(StatusCodes.Status502BadGateway, "Failed to fetch external HLS file");
            }
            catch (TaskCanceledException)
            {
                _logger.LogWarning("Request timeout while fetching external HLS file: {Url}", externalUrl);
                return StatusCode(StatusCodes.Status504GatewayTimeout, "Request timeout");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while fetching external HLS file: {Url}", externalUrl);
                return StatusCode(StatusCodes.Status500InternalServerError, "Internal server error");
            }
        }
    }
}
