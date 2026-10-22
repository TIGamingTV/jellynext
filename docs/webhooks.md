# Webhook Integration

Webhook mode sends an HTTP request to a URL you choose whenever JellyNext requests a movie or season. Use it for download systems other than Radarr/Sonarr/Jellyseerr, for automation, or for notifications. If you use Radarr/Sonarr or Jellyseerr, their dedicated modes are simpler.

## When a webhook is sent

The same request is sent for every way JellyNext requests content:

- a user plays a placeholder in a JellyNext library,
- a user presses **Request** in the New Seasons widget or Modular Home section,
- the hourly watchlist auto-add finds a new watchlist entry.

## Setup

1. **Dashboard → Plugins → JellyNext → Downloads** tab → select **Webhooks**.
2. **HTTP Method**: GET, POST (default), PUT or PATCH. One method is used for both movies and shows.
3. **Movie Webhooks** and **TV Show Webhooks** — for each:
   - **Webhook URL** (required; a request type with no URL fails with "Webhook movie/show URL is not configured").
   - **Custom Headers** (optional): **+ Add Header** for each name/value pair.
   - **Request Payload**: JSON template; **Reset to Default** restores the template below. The placeholder buttons insert placeholders at the cursor.
4. **Save Configuration**.
5. Test by playing an item in a JellyNext library: the client shows a **JellyNext Download** message with the result (some clients do not display server messages; check the log then).

## Placeholders

Placeholders are written in curly braces and are replaced in the URL, header names, header values and payload. Matching is case-insensitive (`{TMDBID}` = `{tmdbId}`). A missing value (for example no IMDb ID) becomes an empty string.

| Placeholder | Movies | Shows | Example |
|---|---|---|---|
| `{tmdbId}` | ✓ | ✓ | `550` |
| `{imdbId}` | ✓ | ✓ | `tt0137523` |
| `{title}` | ✓ | ✓ | `Fight Club` |
| `{year}` | ✓ | ✓ | `1999` |
| `{jellyfinUserId}` | ✓ | ✓ | `a1b2c3d4-e5f6-7890-1234-567890abcdef` — the user who requested (for watchlist auto-add: the watchlist's owner) |
| `{tvdbId}` | | ✓ | `81189` |
| `{seasonNumber}` | | ✓ | `2` |
| `{isAnime}` | | ✓ | `true` / `false` (Trakt genre "anime") |

**Values are inserted verbatim.** They are not URL-encoded and not JSON-escaped:

- Prefer IDs in URLs. A `{title}` containing spaces, `&` or `#` produces a broken query string.
- A title containing `"` or `\` produces invalid JSON in the payload. If your endpoint must parse every request, avoid `{title}` in the payload or tolerate that case.
- Quote string placeholders in JSON (`"{title}"`) and leave `{seasonNumber}` and `{isAnime}` unquoted.

## Default payloads

Movie:

```json
{
  "tmdbId": "{tmdbId}",
  "imdbId": "{imdbId}",
  "title": "{title}",
  "year": "{year}",
  "jellyfinUserId": "{jellyfinUserId}"
}
```

Show:

```json
{
  "tvdbId": "{tvdbId}",
  "tmdbId": "{tmdbId}",
  "imdbId": "{imdbId}",
  "title": "{title}",
  "year": "{year}",
  "seasonNumber": {seasonNumber},
  "isAnime": {isAnime},
  "jellyfinUserId": "{jellyfinUserId}"
}
```

## Request and response

- **Body**: sent only for POST, PUT and PATCH, and only when the payload is not empty. It is always sent with `Content-Type: application/json; charset=utf-8`. A custom `Content-Type` header is ignored.
- **GET**: no body; put everything you need in the URL.
- **Success**: any 2xx status. After playback the user's notification reads "`<title>` webhook triggered successfully (HTTP 200)".
- **Failure**: any other status, or a network error. The notification reads "Webhook request failed: HTTP `<code>` - `<reason>`"; the response body is written to the Jellyfin log.
- The request waits for your response, so respond quickly and do slow work asynchronously.

## Examples

### GET with IDs in the URL

- Method: `GET`
- Movie URL: `https://api.example.com/download?type=movie&tmdb={tmdbId}&user={jellyfinUserId}`
- Show URL: `https://api.example.com/download?type=show&tvdb={tvdbId}&season={seasonNumber}&user={jellyfinUserId}`

### POST with an API key

- Method: `POST`
- Movie URL: `https://api.example.com/media/movie`
- Show URL: `https://api.example.com/media/show`
- Header: `Authorization` = `Bearer YOUR_API_TOKEN`

Movie payload:

```json
{ "mediaType": "movie", "tmdbId": "{tmdbId}", "requestedBy": "{jellyfinUserId}" }
```

Show payload:

```json
{ "mediaType": "show", "tvdbId": "{tvdbId}", "seasonNumber": {seasonNumber}, "requestedBy": "{jellyfinUserId}" }
```

### Discord notification

- Method: `POST`
- URL (movies and shows): `https://discord.com/api/webhooks/<webhook-id>/<webhook-token>`

Movie payload:

```json
{
  "content": "Movie requested",
  "embeds": [{ "title": "{title} ({year})", "description": "TMDB {tmdbId}, user {jellyfinUserId}" }]
}
```

Show payload:

```json
{
  "content": "Season requested",
  "embeds": [{ "title": "{title} ({year}) - Season {seasonNumber}", "description": "TVDB {tvdbId}, anime: {isAnime}" }]
}
```

### Minimal receiving endpoint (Python/Flask)

```python
from flask import Flask, request, jsonify

app = Flask(__name__)

@app.post("/webhook/movie")
def movie():
    data = request.get_json(silent=True) or {}
    if not data.get("tmdbId"):
        return jsonify(error="missing tmdbId"), 400
    # queue the download here
    return jsonify(status="accepted"), 200

@app.post("/webhook/show")
def show():
    data = request.get_json(silent=True) or {}
    if not data.get("tvdbId"):
        return jsonify(error="missing tvdbId"), 400
    # queue data["tvdbId"], data["seasonNumber"] here
    return jsonify(status="accepted"), 200

if __name__ == "__main__":
    app.run(host="0.0.0.0", port=8080)
```

## Troubleshooting

- **Nothing is sent**: check that **Webhooks** is the selected integration and that the URL for that type (movie or show) is filled in, then save.
- **Check the Jellyfin log** (**Dashboard → Logs**) for:
  - `Successfully triggered webhook for movie: ...` / `... for show: ...`
  - `Failed to trigger webhook ...` and `Webhook request failed with status ...` (includes the response body)
  - `Exception while sending webhook to <url>` (DNS, connection refused, TLS errors)
- **Endpoint unreachable**: the request is made by the Jellyfin server, so the URL must be reachable from the server (inside Docker, use the container network name, not `localhost`).
- **Endpoint gets invalid JSON**: see the escaping notes under [Placeholders](#placeholders).

## Security

- Use HTTPS for endpoints outside your local network.
- Authenticate requests with a header such as `Authorization` or `X-Api-Key`, and validate it in your endpoint.
- Header values, including secrets, are stored in plain text in the plugin configuration.
