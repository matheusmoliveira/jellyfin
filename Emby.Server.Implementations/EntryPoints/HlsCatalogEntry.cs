using System.Text.Json.Serialization;

namespace Emby.Server.Implementations.EntryPoints;

/// <summary>
/// Represents a single HLS entry defined in the JSON catalog.
/// </summary>
public sealed class HlsCatalogEntry
{
    /// <summary>
    /// Gets or sets the display title.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the original title.
    /// </summary>
    [JsonPropertyName("originalTitle")]
    public string? OriginalTitle { get; set; }

    /// <summary>
    /// Gets or sets the release year.
    /// </summary>
    [JsonPropertyName("year")]
    public int? Year { get; set; }

    /// <summary>
    /// Gets or sets the overview/plot.
    /// </summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>
    /// Gets or sets the IMDb identifier (ttXXXXXX).
    /// </summary>
    [JsonPropertyName("imdbId")]
    public string? ImdbId { get; set; }

    /// <summary>
    /// Gets or sets the path of the library folder that should contain the virtual item.
    /// </summary>
    [JsonPropertyName("libraryPath")]
    public string? LibraryPath { get; set; }

    /// <summary>
    /// Gets or sets the optional slug used for the external HLS folder.
    /// </summary>
    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    /// <summary>
    /// Gets or sets the TMDb poster path (e.g., "/abc123.jpg").
    /// </summary>
    [JsonPropertyName("posterPath")]
    public string? PosterPath { get; set; }

    /// <summary>
    /// Gets or sets the TMDb backdrop path (e.g., "/xyz789.jpg").
    /// </summary>
    [JsonPropertyName("backdropPath")]
    public string? BackdropPath { get; set; }

    /// <summary>
    /// Gets or sets a direct poster URL (overrides posterPath if set).
    /// </summary>
    [JsonPropertyName("posterUrl")]
    public string? PosterUrl { get; set; }

    /// <summary>
    /// Gets or sets a direct backdrop URL (overrides backdropPath if set).
    /// </summary>
    [JsonPropertyName("backdropUrl")]
    public string? BackdropUrl { get; set; }

    /// <summary>
    /// Gets or sets the external HLS stream URL (e.g., "https://example.com/filmes/slug/hls/master.m3u8").
    /// If not provided, will be auto-generated from the slug.
    /// </summary>
    [JsonPropertyName("externalUrl")]
    public string? ExternalUrl { get; set; }
}
