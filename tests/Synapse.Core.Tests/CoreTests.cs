using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace Synapse.Tests;

public class ThinkTagSplitterTests
{
    [Fact]
    public void SplitsTagsAcrossChunks()
    {
        var s = new ThinkTagSplitter();
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        foreach (var chunk in new[] { "Hi <thi", "nk>secret ", "plan</th", "ink> answer", " <thought>x</thought>done" })
        {
            var (c, r) = s.Feed(chunk);
            content.Append(c);
            reasoning.Append(r);
        }
        var rest = s.Flush();
        content.Append(rest.Content);
        reasoning.Append(rest.Reasoning);
        Assert.Equal("Hi  answer done", content.ToString());
        Assert.Equal("secret planx", reasoning.ToString());
    }

    [Fact]
    public void UnclosedThinkingStaysReasoning()
    {
        var s = new ThinkTagSplitter();
        var (c, r) = s.Feed("<think>still going");
        var rest = s.Flush();
        Assert.Equal("", c + rest.Content);
        Assert.Equal("still going", r + rest.Reasoning);
    }
}

public class ToolCallMergeTests
{
    [Fact]
    public void AssemblesIndexedDeltas()
    {
        var calls = new List<ToolCall>();
        ChatClient.MergeToolCalls(calls, (JsonArray)JsonNode.Parse("""[{"index":0,"id":"a","function":{"name":"web_search","arguments":"{\"q"}}]""")!);
        ChatClient.MergeToolCalls(calls, (JsonArray)JsonNode.Parse("""[{"index":0,"function":{"arguments":"uery\":\"x\"}"}},{"index":1,"id":"b","function":{"name":"fetch_url","arguments":"{}"}}]""")!);
        Assert.Equal(2, calls.Count);
        Assert.Equal("""{"query":"x"}""", calls[0].Arguments);
        Assert.Equal("fetch_url", calls[1].Name);
    }

    [Fact]
    public void AssemblesIdOnlyDeltasAndKeepsExtra()
    {
        var calls = new List<ToolCall>();
        ChatClient.MergeToolCalls(calls, (JsonArray)JsonNode.Parse("""[{"id":"x1","function":{"name":"a","arguments":"{}"},"extra_content":{"google":{"thought_signature":"sig"}}}]""")!);
        ChatClient.MergeToolCalls(calls, (JsonArray)JsonNode.Parse("""[{"id":"x2","function":{"name":"b","arguments":"{}"}}]""")!);
        Assert.Equal(["a", "b"], calls.Select(c => c.Name));
        Assert.Equal("sig", calls[0].Extra?["google"]?["thought_signature"]?.GetValue<string>());
    }
}

public class HuggingFaceTests
{
    [Fact]
    public void GroupsSplitFilesAndFindsProjector()
    {
        var files = new List<HfFile>
        {
            new("Model-Q4_K_M.gguf", 4_000),
            new("Model-Q8_0-00001-of-00002.gguf", 5_000),
            new("Model-Q8_0-00002-of-00002.gguf", 5_000),
            new("mmproj-F16.gguf", 600),
            new("mmproj-BF16.gguf", 600),
            new("README.md", 10),
        };
        var v = HuggingFace.Variants("org/Model-GGUF", files);
        Assert.Equal(["Q4_K_M", "Q8_0"], v.Select(x => x.Label));
        Assert.Equal(2, v[1].Files.Count);
        Assert.Equal("mmproj-F16.gguf", v[0].Mmproj);
        Assert.Equal(10_600, v[1].SizeBytes);
        Assert.Equal("Model-Q8_0-00001-of-00002.gguf", v[1].MainFile);
    }

    [Theory]
    [InlineData("Qwen3.5-4B-UD-Q4_K_XL.gguf", "UD-Q4_K_XL")]
    [InlineData("sub/gpt-oss-20b-MXFP4.gguf", "MXFP4")]
    [InlineData("gemma-4-E2B-it-BF16.gguf", "BF16")]
    [InlineData("Phi-4-mini-instruct-IQ4_XS.gguf", "IQ4_XS")]
    public void QuantLabels(string path, string label) => Assert.Equal(label, HuggingFace.QuantLabel(path));

    [Fact]
    public void RecommendsBestQuantThatFits()
    {
        const long GB = 1L << 30;
        var hw = new HardwareInfo("cpu", 16 * GB, 8, 500 * GB, [new GpuInfo("RTX", 8 * GB, "nvidia")], "x64");
        var variants = new List<ModelVariant>
        {
            new() { Repo = "r", Label = "Q2_K", SizeBytes = 3 * GB },
            new() { Repo = "r", Label = "Q4_K_M", SizeBytes = 5 * GB },
            new() { Repo = "r", Label = "Q8_0", SizeBytes = 9 * GB },
            new() { Repo = "r", Label = "F16", SizeBytes = 17 * GB },
        };
        Assert.Equal("Q4_K_M", HuggingFace.RecommendedVariant(variants, hw)?.Label);
        Assert.Equal(Fit.Great, hw.FitFor(5 * GB));
        Assert.Equal(Fit.TooLarge, hw.FitFor(30 * GB));
    }
}

public class PathTests
{
    [Fact]
    public void InsideComparesWholeSegments()
    {
        var root = Path.Combine(Path.GetTempPath(), "proj");
        Assert.True(Util.IsInside(Path.Combine(root, "a", "b.txt"), root));
        Assert.True(Util.IsInside(root, root));
        Assert.False(Util.IsInside(root + "2" + Path.DirectorySeparatorChar + "x", root));
        Assert.False(Util.IsInside(Path.Combine(root, "..", "other"), root));
    }

    [Fact]
    public void Slugs()
    {
        Assert.Equal("coffee-shop-landing", Util.Slug("Coffee Shop — Landing!"));
        Assert.Equal("site", Util.Slug("!!!"));
    }
}

public class LlamaAssetTests
{
    static readonly LocalEngine.ReleaseAsset[] Assets =
    [
        new("llama-b7000-bin-win-cpu-x64.zip", "u1", 30),
        new("llama-b7000-bin-win-cpu-arm64.zip", "u2", 30),
        new("llama-b7000-bin-win-cuda-12.4-x64.zip", "u3", 300),
        new("cudart-llama-bin-win-cuda-12.4-x64.zip", "u4", 400),
        new("llama-b7000-bin-win-cuda-13.4-x64.zip", "u5", 300),
        new("cudart-llama-bin-win-cuda-13.4-x64.zip", "u6", 400),
        new("llama-b7000-bin-win-vulkan-x64.zip", "u7", 40),
        new("llama-b7000-bin-win-rocm-10.0-x64.zip", "u8", 500),
        new("llama-b7000-bin-ubuntu-x64.tar.gz", "u9", 30),
    ];

    [Theory]
    [InlineData("cuda-12", "x64", "llama-b7000-bin-win-cuda-12.4-x64.zip,cudart-llama-bin-win-cuda-12.4-x64.zip")]
    [InlineData("cuda-13", "x64", "llama-b7000-bin-win-cuda-13.4-x64.zip,cudart-llama-bin-win-cuda-13.4-x64.zip")]
    [InlineData("vulkan", "x64", "llama-b7000-bin-win-vulkan-x64.zip")]
    [InlineData("rocm", "x64", "llama-b7000-bin-win-rocm-10.0-x64.zip")]
    [InlineData("cpu", "arm64", "llama-b7000-bin-win-cpu-arm64.zip")]
    [InlineData("vulkan", "arm64", "llama-b7000-bin-win-cpu-arm64.zip")]
    public void PicksTheRightZips(string backend, string arch, string expected) =>
        Assert.Equal(expected, string.Join(",", LocalEngine.PickAssets(Assets, backend, arch).Select(a => a.Name)));

    [Fact]
    public void AutoBackendFollowsTheGpu()
    {
        const long GB = 1L << 30;
        Assert.Equal("cuda-12", LocalEngine.AutoBackend(new HardwareInfo("c", 16 * GB, 8, 0, [new GpuInfo("NVIDIA GeForce RTX 4070", 12 * GB, "nvidia")], "x64")));
        Assert.Equal("vulkan", LocalEngine.AutoBackend(new HardwareInfo("c", 16 * GB, 8, 0, [new GpuInfo("AMD Radeon RX 7800", 16 * GB, "amd")], "x64")));
        Assert.Equal("cpu", LocalEngine.AutoBackend(new HardwareInfo("c", 16 * GB, 8, 0, [new GpuInfo("Intel UHD", 128L << 20, "intel")], "x64")));
    }
}

public class TextExtractTests
{
    [Fact]
    public void ReadsWordDocuments()
    {
        var path = Path.Combine(Path.GetTempPath(), $"synapse-test-{Guid.NewGuid():N}.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("word/document.xml");
            using var w = new StreamWriter(e.Open());
            w.Write("""
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
                <w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>Title</w:t></w:r></w:p>
                <w:p><w:r><w:t>Hello </w:t></w:r><w:r><w:t>world</w:t></w:r></w:p>
                </w:body></w:document>
                """);
        }
        try { Assert.Equal("# Title\nHello world", TextExtract.Read(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadsRtf() =>
        Assert.Equal("Hello bold\nnext", TextExtract.Rtf(@"{\rtf1\ansi{\fonttbl{\f0 Arial;}}\f0 Hello \b bold\b0\par next}"));

    [Fact]
    public void DetectsBinary() => Assert.True(TextExtract.LooksBinary([1, 2, 0, 3]));
}

public class SearchParsingTests
{
    [Fact]
    public void ParsesDuckDuckGoResults()
    {
        var html = """
            <div class="result__body"><a class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fpage&amp;rut=1">Example &amp; Co</a>
            <a class="result__snippet" href="#">A <b>snippet</b></a></div>
            <div class="result__body"><a class="result__a" href="https://duckduckgo.com/y.js?ad=1">Ad</a></div>
            """;
        var r = Tools.ParseSearchResults(html);
        Assert.Single(r);
        Assert.Equal(("Example & Co", "https://example.com/page", "A snippet"), r[0]);
    }

    [Fact]
    public void BrowserNormalizesInput()
    {
        Assert.Equal("https://example.com/", BrowserAgent.Normalize("example.com"));
        Assert.Equal("http://localhost:5500/", BrowserAgent.Normalize("localhost:5500"));
        Assert.StartsWith("https://duckduckgo.com/?q=best%20pizza", BrowserAgent.Normalize("best pizza"));
    }
}

public class LlamaFallbackTests
{
    [Theory]
    [InlineData("cuda-12", "x64", "llama-b9000-bin-win-cuda-12.4-x64.zip,cudart-llama-bin-win-cuda-12.4-x64.zip")]
    [InlineData("cuda-13", "arm64", "llama-b9000-bin-win-cuda-13.4-arm64.zip,cudart-llama-bin-win-cuda-13.4-arm64.zip")]
    [InlineData("vulkan", "x64", "llama-b9000-bin-win-vulkan-x64.zip")]
    [InlineData("cpu", "x64", "llama-b9000-bin-win-cpu-x64.zip")]
    public void GuessedAssetsMatchTheReleaseNames(string backend, string arch, string expected)
    {
        var assets = LocalEngine.GuessAssets("b9000");
        var picked = LocalEngine.PickAssets(assets, backend, arch);
        Assert.Equal(expected, string.Join(",", picked.Select(a => a.Name)));
        Assert.All(picked, a => Assert.StartsWith("https://github.com/ggml-org/llama.cpp/releases/download/", a.Url));
    }
}
