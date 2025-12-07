using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Public endpoint to list HLS catalog entries without requiring library scan.
/// </summary>
[ApiController]
[Route("[controller]")]
public class HlsCatalogController : ControllerBase
{
    private const string CatalogFileName = "hls-catalog.json";

    private static readonly JsonSerializerOptions CatalogSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationHost _appHost;
    private readonly IServerApplicationPaths _appPaths;
    private readonly ILogger<HlsCatalogController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsCatalogController"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="appHost">The application host.</param>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public HlsCatalogController(
        ILibraryManager libraryManager,
        IServerApplicationHost appHost,
        IServerApplicationPaths appPaths,
        ILogger<HlsCatalogController> logger)
    {
        _libraryManager = libraryManager;
        _appHost = appHost;
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <summary>
    /// Returns the HLS catalog entries with direct playback URLs.
    /// </summary>
    /// <returns>The catalog list.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IEnumerable<object>> GetCatalog()
    {
        try
        {
            var catalogPath = Path.Combine(_appPaths.DataPath, CatalogFileName);
            if (!System.IO.File.Exists(catalogPath))
            {
                return NotFound($"Catalog '{catalogPath}' not found.");
            }

            var raw = System.IO.File.ReadAllText(catalogPath);
            var entries = JsonSerializer.Deserialize<List<HlsCatalogDto>>(raw, CatalogSerializerOptions);

            var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";

            var result = new List<object>();
            if (entries != null)
            {
                foreach (var e in entries)
                {
                    if (string.IsNullOrWhiteSpace(e.Slug))
                    {
                        continue;
                    }

                    // Busca o item na biblioteca para obter o ItemId
                    BaseItem? item = null;
                    if (!string.IsNullOrWhiteSpace(e.ImdbId))
                    {
                        item = FindByImdb(e.ImdbId);
                    }

                    // Fallback: tenta buscar pelo Slug se não encontrou por IMDb
                    if (item == null && !string.IsNullOrWhiteSpace(e.Slug))
                    {
                        item = FindBySlug(e.Slug);
                    }

                    var poster = TryGetPosterUrl(e);
                    var backdrop = TryGetBackdropUrl(e);

                    _logger.LogDebug("HLS Catalog entry: {Title}, PosterUrl: {Poster}, BackdropUrl: {Backdrop}, ItemId: {ItemId}", e.Title, poster ?? "null", backdrop ?? "null", item?.Id.ToString() ?? "null");

                    result.Add(new
                    {
                        e.Title,
                        e.OriginalTitle,
                        e.Year,
                        e.Overview,
                        e.ImdbId,
                        e.Slug,
                        PlaybackUrl = $"{baseUrl}/HlsExternal/{e.Slug}/master.m3u8",
                        PosterUrl = poster,
                        BackdropUrl = backdrop,
                        PosterPath = e.PosterPath,
                        BackdropPath = e.BackdropPath,
                        ItemId = item?.Id.ToString()
                    });
                }
            }

            try
            {
                // Logamos o payload completo para confirmar exatamente o que está sendo retornado pelo backend.
                var preview = JsonSerializer.Serialize(result, CatalogSerializerOptions);
                _logger.LogWarning("HLS Catalog payload being returned: {Payload}", preview);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to serialize HLS catalog payload for logging.");
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
        }
    }

    private string? TryGetPosterUrl(HlsCatalogDto entry)
    {
        // Prioridade: URL direta do catalog -> imagens já baixadas pelo Jellyfin (via TMDb refresh)
        if (!string.IsNullOrWhiteSpace(entry.PosterUrl))
        {
            return entry.PosterUrl;
        }

        // Busca imagens já baixadas pelo Jellyfin (via refresh de metadata do TMDb)
        if (!string.IsNullOrWhiteSpace(entry.ImdbId))
        {
            var item = FindByImdb(entry.ImdbId);
            if (item != null)
            {
                // Verifica se tem imagem, mas retorna a URL mesmo se ainda não tiver (o Jellyfin vai servir ou retornar placeholder)
                if (item.HasImage(ImageType.Primary, 0))
                {
                    var url = BuildImageUrl(item.Id, "Primary");
                    _logger.LogDebug("Found poster image for {Title} (IMDb: {ImdbId})", entry.Title, entry.ImdbId);
                    return url;
                }
                else
                {
                    // Retorna a URL mesmo sem imagem - o Jellyfin pode retornar um placeholder ou a imagem pode estar sendo baixada
                    var url = BuildImageUrl(item.Id, "Primary");
                    _logger.LogDebug("Item {Title} (IMDb: {ImdbId}) found but no poster image yet. URL: {Url}", entry.Title, entry.ImdbId, url);
                    return url;
                }
            }
            else
            {
                _logger.LogDebug("Item not found for IMDb ID: {ImdbId}", entry.ImdbId);
            }
        }

        return null;
    }

    private string? TryGetBackdropUrl(HlsCatalogDto entry)
    {
        // Prioridade: URL direta do catalog -> imagens já baixadas pelo Jellyfin (via TMDb refresh)
        if (!string.IsNullOrWhiteSpace(entry.BackdropUrl))
        {
            return entry.BackdropUrl;
        }

        // Busca imagens já baixadas pelo Jellyfin (via refresh de metadata do TMDb)
        if (!string.IsNullOrWhiteSpace(entry.ImdbId))
        {
            var item = FindByImdb(entry.ImdbId);
            if (item != null)
            {
                // Verifica se tem imagem, mas retorna a URL mesmo se ainda não tiver (o Jellyfin vai servir ou retornar placeholder)
                if (item.HasImage(ImageType.Backdrop, 0))
                {
                    var url = BuildImageUrl(item.Id, "Backdrop");
                    _logger.LogDebug("Found backdrop image for {Title} (IMDb: {ImdbId})", entry.Title, entry.ImdbId);
                    return url;
                }
                else
                {
                    // Retorna a URL mesmo sem imagem - o Jellyfin pode retornar um placeholder ou a imagem pode estar sendo baixada
                    var url = BuildImageUrl(item.Id, "Backdrop");
                    _logger.LogDebug("Item {Title} (IMDb: {ImdbId}) found but no backdrop image yet. URL: {Url}", entry.Title, entry.ImdbId, url);
                    return url;
                }
            }
        }

        return null;
    }

    private BaseItem? FindByImdb(string imdbId)
    {
        // Método 1: Busca usando InternalItemsQuery (padrão)
        // IMPORTANTE: Não define IsVirtualItem para incluir tanto itens virtuais quanto não-virtuais
        var query = new InternalItemsQuery
        {
            HasAnyProviderId = new Dictionary<string, string>
            {
                { MediaBrowser.Model.Entities.MetadataProvider.Imdb.ToString(), imdbId }
            },
            Limit = 1,
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            Recursive = true
            // IsVirtualItem não é definido (null) para incluir todos os itens
        };

        var items = _libraryManager.GetItemList(query);
        var item = items.FirstOrDefault();

        _logger.LogDebug("FindByImdb query for {ImdbId} returned {Count} items", imdbId, items.Count);

        if (item != null)
        {
            _logger.LogDebug("Found item for IMDb ID: {ImdbId}, ItemId: {ItemId}, Name: {Name}", imdbId, item.Id, item.Name);
            return item;
        }

        // Método 2: Busca alternativa - lista todos os filmes e verifica ProviderIds manualmente
        // IMPORTANTE: Não define IsVirtualItem para incluir tanto itens virtuais quanto não-virtuais
        _logger.LogDebug("Trying alternative search method for IMDb ID: {ImdbId}", imdbId);
        var allMoviesQuery = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            Recursive = true,
            Limit = 1000, // Limite razoável para busca manual
            // IsVirtualItem não é definido (null) para incluir todos os itens
        };

        var allMovies = _libraryManager.GetItemList(allMoviesQuery);
        _logger.LogDebug("Found {Count} total movies in library (FindByImdb manual)", allMovies.Count);
        // Loga todos os filmes para depuração (número é pequeno ~21)
        try
        {
            var listInfo = allMovies.Select(m =>
            {
                var hasImdbLocal = m.TryGetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb, out var imdbLocal);
                var slugLocal = m.ProviderIds != null && m.ProviderIds.TryGetValue("HlsSlug", out var slugVal) ? slugVal : null;
                return $"{m.Name} [{m.Id}] imdb:{(hasImdbLocal ? imdbLocal : "null")} slug:{slugLocal ?? "null"}";
            });
            _logger.LogWarning("FindByImdb manual list: {List}", string.Join(" | ", listInfo));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log movie list for FindByImdb");
        }

        // Log dos primeiros 10 filmes para debug
        var firstMovies = allMovies.Take(10).ToList();
        foreach (var movie in firstMovies)
        {
            var hasImdb = movie.TryGetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb, out var movieImdbId);
            string? movieSlugValue = null;
            var hasSlug = movie.ProviderIds?.TryGetValue("HlsSlug", out movieSlugValue) == true;
            var slugValue = hasSlug && movieSlugValue != null ? movieSlugValue : "null";
            var imdbValue = hasImdb ? movieImdbId : "null";
            _logger.LogDebug(
                "Movie: {Name}, ID: {Id}, HasImdb: {HasImdb}, ImdbId: {ImdbId}, HasSlug: {HasSlug}, Slug: {Slug}",
                movie.Name,
                movie.Id,
                hasImdb,
                imdbValue,
                hasSlug,
                slugValue);
        }

        foreach (var movie in allMovies)
        {
            if (movie.TryGetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb, out var movieImdbId))
            {
                if (string.Equals(movieImdbId, imdbId, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Found item via manual search for IMDb ID: {ImdbId}, ItemId: {ItemId}, Name: {Name}", imdbId, movie.Id, movie.Name);
                    return movie;
                }
            }
        }

        _logger.LogWarning("Item not found for IMDb ID: {ImdbId} using both search methods. Total movies searched: {Count}", imdbId, allMovies.Count);

        // Método 3: Tenta buscar diretamente pelo Path virtual (último recurso)
        // Isso pode ajudar se o item virtual não estiver sendo indexado corretamente
        _logger.LogDebug("Trying to find item by virtual path for IMDb ID: {ImdbId}", imdbId);
        try
        {
            // Tenta encontrar o item pelo caminho virtual esperado
            // O caminho virtual é construído como: {libraryPath}/{slug}.virtual
            var allItems = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                Recursive = true,
                Limit = 10000 // Busca mais ampla possível
            });

            _logger.LogDebug("Direct path search found {Count} total items", allItems.Count);

            foreach (var candidate in allItems)
            {
                if (candidate.TryGetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb, out var candidateImdbId))
                {
                    if (string.Equals(candidateImdbId, imdbId, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogDebug("Found item via direct path search for IMDb ID: {ImdbId}, ItemId: {ItemId}, Name: {Name}, Path: {Path}, IsVirtual: {IsVirtual}", imdbId, candidate.Id, candidate.Name, candidate.Path ?? "null", candidate.IsVirtualItem);
                        return candidate;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during direct path search for IMDb ID: {ImdbId}", imdbId);
        }

        return null;
    }

    private BaseItem? FindBySlug(string slug)
    {
        const string HlsSlugProviderKey = "HlsSlug";

        // Método 1: Busca usando InternalItemsQuery (pode não funcionar para ProviderIds customizados)
        // IMPORTANTE: Não define IsVirtualItem para incluir tanto itens virtuais quanto não-virtuais
        var query = new InternalItemsQuery
        {
            HasAnyProviderId = new Dictionary<string, string>
            {
                { HlsSlugProviderKey, slug }
            },
            Limit = 1,
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            Recursive = true
            // IsVirtualItem não é definido (null) para incluir todos os itens
        };

        var items = _libraryManager.GetItemList(query);
        var item = items.FirstOrDefault();

        _logger.LogDebug("FindBySlug query for {Slug} returned {Count} items", slug, items.Count);

        if (item != null)
        {
            _logger.LogDebug("Found item for Slug: {Slug}, ItemId: {ItemId}, Name: {Name}", slug, item.Id, item.Name);
            return item;
        }

        // Método 2: Busca alternativa - lista todos os filmes e verifica ProviderIds manualmente
        // IMPORTANTE: Não define IsVirtualItem para incluir tanto itens virtuais quanto não-virtuais
        _logger.LogDebug("Trying alternative search method for Slug: {Slug}", slug);
        var allMoviesQuery = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            Recursive = true,
            Limit = 1000, // Limite razoável para busca manual
            // IsVirtualItem não é definido (null) para incluir todos os itens
        };

        var allMovies = _libraryManager.GetItemList(allMoviesQuery);
        _logger.LogDebug("Found {Count} total movies in library for Slug search", allMovies.Count);
        // Loga todos os filmes para depuração (número é pequeno ~21)
        try
        {
            var listInfo = allMovies.Select(m =>
            {
                var hasImdbLocal = m.TryGetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb, out var imdbLocal);
                var slugLocal = m.ProviderIds != null && m.ProviderIds.TryGetValue(HlsSlugProviderKey, out var slugVal) ? slugVal : null;
                return $"{m.Name} [{m.Id}] imdb:{(hasImdbLocal ? imdbLocal : "null")} slug:{slugLocal ?? "null"}";
            });
            _logger.LogWarning("FindBySlug manual list: {List}", string.Join(" | ", listInfo));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log movie list for FindBySlug");
        }

        // Log dos primeiros 10 filmes que têm HlsSlug para debug
        var moviesWithSlug = allMovies.Where(m => m.ProviderIds != null && m.ProviderIds.ContainsKey(HlsSlugProviderKey)).Take(10).ToList();
        _logger.LogDebug("Found {Count} movies with HlsSlug ProviderId", moviesWithSlug.Count);
        foreach (var movie in moviesWithSlug)
        {
            var movieSlug = movie.ProviderIds?[HlsSlugProviderKey];
            var hasImdb = movie.TryGetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb, out var movieImdbId);
            var imdbValue = hasImdb ? movieImdbId : "null";
            _logger.LogDebug(
                "Movie with Slug: {Name}, ID: {Id}, Slug: {Slug}, ImdbId: {ImdbId}",
                movie.Name,
                movie.Id,
                movieSlug ?? "null",
                imdbValue);
        }

        foreach (var movie in allMovies)
        {
            // Verifica se o filme tem o ProviderId HlsSlug
            if (movie.ProviderIds != null && movie.ProviderIds.TryGetValue(HlsSlugProviderKey, out var movieSlug))
            {
                if (string.Equals(movieSlug, slug, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Found item via manual search for Slug: {Slug}, ItemId: {ItemId}, Name: {Name}", slug, movie.Id, movie.Name);
                    return movie;
                }
            }
        }

        _logger.LogWarning(
            "Item not found for Slug: {Slug} using both search methods. Total movies searched: {Count}, Movies with HlsSlug: {SlugCount}",
            slug,
            allMovies.Count,
            moviesWithSlug.Count);

        // Método 3: Tenta buscar diretamente pelo Path virtual (último recurso)
        // Isso pode ajudar se o item virtual não estiver sendo indexado corretamente
        _logger.LogDebug("Trying to find item by virtual path for Slug: {Slug}", slug);
        try
        {
            // Tenta encontrar o item pelo caminho virtual esperado
            // O caminho virtual é construído como: {libraryPath}/{slug}.virtual
            var allItems = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                Recursive = true,
                Limit = 10000 // Busca mais ampla possível
            });

            _logger.LogDebug("Direct path search for Slug found {Count} total items", allItems.Count);

            foreach (var candidate in allItems)
            {
                if (candidate.ProviderIds != null && candidate.ProviderIds.TryGetValue(HlsSlugProviderKey, out var candidateSlug))
                {
                    if (string.Equals(candidateSlug, slug, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogDebug("Found item via direct path search for Slug: {Slug}, ItemId: {ItemId}, Name: {Name}, Path: {Path}, IsVirtual: {IsVirtual}", slug, candidate.Id, candidate.Name, candidate.Path ?? "null", candidate.IsVirtualItem);
                        return candidate;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during direct path search for Slug: {Slug}", slug);
        }

        return null;
    }

    private string BuildImageUrl(Guid itemId, string type)
    {
        var api = _appHost.GetApiUrlForLocalAccess();
        return $"{api}/Items/{itemId}/Images/{type}";
    }

    /// <summary>
    /// DTO for HLS catalog entries (mirrors HlsCatalogEntry from Emby.Server.Implementations).
    /// </summary>
    private sealed class HlsCatalogDto
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("originalTitle")]
        public string? OriginalTitle { get; set; }

        [JsonPropertyName("year")]
        public int? Year { get; set; }

        [JsonPropertyName("overview")]
        public string? Overview { get; set; }

        [JsonPropertyName("imdbId")]
        public string? ImdbId { get; set; }

        [JsonPropertyName("libraryPath")]
        public string? LibraryPath { get; set; }

        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        [JsonPropertyName("posterPath")]
        public string? PosterPath { get; set; }

        [JsonPropertyName("backdropPath")]
        public string? BackdropPath { get; set; }

        [JsonPropertyName("posterUrl")]
        public string? PosterUrl { get; set; }

        [JsonPropertyName("backdropUrl")]
        public string? BackdropUrl { get; set; }
    }
}
