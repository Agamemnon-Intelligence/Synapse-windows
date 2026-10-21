using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Synapse;

public static class ImageGen
{
    public static async Task<ToolResult> Generate(AppState app, string prompt, string aspect, CancellationToken ct)
    {
        var (w, h) = aspect == "landscape" ? (1344, 768) : aspect == "portrait" ? (768, 1344) : (1024, 1024);
        var file = Path.Combine(Paths.Images, $"image-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Guid.NewGuid().ToString("N")[..4]}.png");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            byte[] bytes;
            switch (app.Settings.ImageProvider)
            {
                case ImageProvider.Pollinations:
                {
                    var url = $"https://image.pollinations.ai/prompt/{Uri.EscapeDataString(prompt)}?width={w}&height={h}&nologo=true&seed={Random.Shared.Next(1, 999_999)}";
                    using var resp = await ChatClient.Http.GetAsync(url, cts.Token);
                    bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
                    if (!resp.IsSuccessStatusCode || !LooksLikeImage(bytes))
                        throw new ApiException((int)resp.StatusCode, "Image service returned no image");
                    break;
                }
                case ImageProvider.Gemini:
                {
                    var key = app.Platform.Secrets.Get("google") ?? throw new ApiException(0, "Add a Google AI Studio key first.");
                    var model = app.Settings.GeminiImageModel;
                    using var req = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent");
                    req.Headers.TryAddWithoutValidation("x-goog-api-key", key);
                    var ratio = aspect == "landscape" ? "16:9" : aspect == "portrait" ? "9:16" : "1:1";
                    var body = new JsonObject
                    {
                        ["contents"] = new JsonArray(new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt }) }),
                        ["generationConfig"] = new JsonObject
                        {
                            ["responseModalities"] = new JsonArray("TEXT", "IMAGE"),
                            ["imageConfig"] = new JsonObject { ["aspectRatio"] = ratio },
                        },
                    };
                    req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                    using var resp = await ChatClient.Http.SendAsync(req, cts.Token);
                    var text = await resp.Content.ReadAsStringAsync(cts.Token);
                    if (!resp.IsSuccessStatusCode) throw new ApiException((int)resp.StatusCode, ChatClient.ErrorMessage(text));
                    var parts = JsonNodeExt.ParseOrNull(text)?["candidates"]?[0]?["content"]?["parts"] as JsonArray;
                    var b64 = parts?.Select(p => p?["inlineData"]?["data"]?.GetValue<string>()).FirstOrDefault(x => x != null)
                              ?? throw new ApiException(0, "Gemini returned no image");
                    bytes = Convert.FromBase64String(b64);
                    break;
                }
                default:
                {
                    // Any OpenAI-compatible images endpoint on the custom server (OpenAI, LocalAI, etc).
                    var body = new JsonObject
                    {
                        ["model"] = app.Settings.OpenaiImageModel,
                        ["prompt"] = prompt,
                        ["size"] = w == h ? "1024x1024" : w > h ? "1536x1024" : "1024x1536",
                        ["n"] = 1,
                    };
                    using var req = ChatClient.MakeRequest(ProviderId.Custom, "/images/generations", body);
                    using var resp = await ChatClient.Http.SendAsync(req, cts.Token);
                    var text = await resp.Content.ReadAsStringAsync(cts.Token);
                    if (!resp.IsSuccessStatusCode) throw new ApiException((int)resp.StatusCode, ChatClient.ErrorMessage(text));
                    var item = JsonNodeExt.ParseOrNull(text)?["data"]?[0];
                    if (item.StrOrNull("b64_json") is { Length: > 0 } b64) bytes = Convert.FromBase64String(b64);
                    else if (item.StrOrNull("url") is { Length: > 0 } u) bytes = await ChatClient.Http.GetByteArrayAsync(u, cts.Token);
                    else throw new ApiException(0, "The server returned no image");
                    break;
                }
            }
            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8) file = Path.ChangeExtension(file, ".jpg");
            else if (bytes.Length >= 4 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F') file = Path.ChangeExtension(file, ".webp");
            await File.WriteAllBytesAsync(file, bytes, CancellationToken.None);
            return new ToolResult($"Image generated and shown to the user: {file}") { Images = [file] };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new ToolResult("Image generation timed out."); }
        catch (Exception e)
        {
            return new ToolResult("Image generation failed: " + (e is ApiException a ? a.Describe() : e.Message));
        }
    }

    static bool LooksLikeImage(byte[] b) =>
        b.Length > 8 && ((b[0] == 0x89 && b[1] == 'P') || (b[0] == 0xFF && b[1] == 0xD8) || (b[0] == 'R' && b[1] == 'I') || (b[0] == 'G' && b[1] == 'I'));
}
