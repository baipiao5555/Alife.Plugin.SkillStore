using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.Extensions.Logging;

namespace BDFFZI.MaoMao.SkillStore;

public class SkillStoreConfig
{
    [DisplayName("市场源")]
    [Description("当前选择使用的市场源，从下拉中选取")]
    public string Sources { get; set; } = "https://modelscope.cn/skills";

    [DisplayName("自定义市场源")]
    [Description("自己添加的市场源，逗号分隔，会出现在市场源下拉里供选择")]
    public string CustomSources { get; set; } = "";

    [DisplayName("翻译服务")]
    [Description("翻译服务：mymemory=免费MyMemory；google=谷歌；baidu=百度翻译；youdao=有道智云；deepl=DeepL；custom=自定义接口")]
    public string TranslationProvider { get; set; } = "mymemory";

    [DisplayName("翻译 API Key")]
    [Description("百度填 appid；有道填 appKey；DeepL 填 API Key；mymemory/google/custom 不用")]
    public string TranslationApiKey { get; set; } = "";

    [DisplayName("翻译 API Secret")]
    [Description("百度填 secret；有道填 appSecret；其它不用")]
    public string TranslationApiSecret { get; set; } = "";

    [DisplayName("自定义翻译接口")]
    [Description("翻译服务选 custom 时使用，{text} 会被替换为要翻译的内容（URL 编码）")]
    public string TranslationApi { get; set; } = "https://api.mymemory.translated.net/get?q={text}&langpair=en|zh-CN";

    [DisplayName("自定义翻译结果字段")]
    [Description("翻译服务选 custom 时，从返回 JSON 中取译文的字段路径，点号分隔。默认 responseData.translatedText")]
    public string TranslationResultPath { get; set; } = "responseData.translatedText";
}

public sealed class SkillInfo
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Path { get; set; } = "";
    public string Description { get; set; } = "";
    public string Content { get; set; } = "";
    public string Url { get; set; } = "";
}

[Module("Skill 商店",
    "从 GitHub、Gitee、GitLab、Codeberg/Gitea、魔搭等市场源浏览并下载 Skill 到本地 Skills 目录，支持魔搭 Skill 中心，下载后即可用 StudySkill 使用。",
    editorUI: typeof(SkillStoreUI),
    defaultCategory: "猫猫的小工具")]
public class SkillStoreModule(
    XmlFunctionCaller functionCaller,
    ILogger<SkillStoreModule> logger,
    Interactor<SkillStoreModule> interactor
) : ChatBehaviour, IConfigurable<SkillStoreConfig>
{
    public SkillStoreConfig Configuration { get; set; } = null!;

    private static readonly string SkillsPath = Path.Combine(AlifePath.StorageFolderPath, "Skills");

    private enum ProviderKind
    {
        GitHubLike, // GitHub / Gitee / Gitea / Forgejo（同一套 contents API，仅 apiBase 不同）
        GitLab,
        ModelScope, // 魔搭模型仓库（/api/v1/models/{o}/{r}/...）
        ModelScopeSkills // 魔搭 Skill 中心（https://modelscope.cn/skills）
    }

    private sealed class SkillSource
    {
        public ProviderKind Provider;
        public string Owner = "";
        public string Repo = "";
        public string ApiBase = "";
    }

    protected override Task OnAwake()
    {
        XmlHandler xmlHandler = new(this)
        {
            Description = "Skill 商店：从 GitHub、Gitee、GitLab、Codeberg/Gitea、魔搭等市场源浏览、下载并安装 Skill 到本地 Skills 目录。",
        };
        functionCaller.RegisterHandler(xmlHandler, cancellationToken: DestroyCancellationToken);
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("列出指定市场源中的全部 Skill（名称 + 用途说明）")]
    public async Task ListMarketSkills(
        [Description("市场源：owner/repo（GitHub）、gitee:/gitlab:/codeberg:/modelscope: 前缀、仓库 URL，或 https://modelscope.cn/skills")] string source)
    {
        try
        {
            List<SkillInfo> skills = await FetchSkills(source);
            if (skills.Count == 0)
            {
                interactor.Poke($"喵，{source} 里暂时没找到 Skill。");
                return;
            }

            var lines = new List<string>();
            foreach (SkillInfo skill in skills)
            {
                string shown = string.IsNullOrWhiteSpace(skill.DisplayName) ? skill.Name : skill.DisplayName;
                lines.Add(string.IsNullOrWhiteSpace(skill.Description)
                    ? shown
                    : $"{shown}：{skill.Description}");
            }
            interactor.Poke($"喵，{source} 里的 Skill：\n- " + string.Join("\n- ", lines));
        }
        catch (Exception ex)
        {
            interactor.Poke($"喵，获取 {source} 的 Skill 列表失败：{ex.Message}");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("从指定市场源下载并安装一个 Skill 到本地 Skills 目录")]
    public async Task InstallSkill(
        [Description("市场源：owner/repo（GitHub）、gitee:/gitlab:/codeberg:/modelscope: 前缀、仓库 URL，或 https://modelscope.cn/skills")] string source,
        [Description("Skill 名称（GitHub 源为目录名，魔搭中心为 Path/Name）")] string skillName)
    {
        try
        {
            string content = await FetchSkillDoc(source, skillName);
            string localName = skillName;
            int slash = localName.IndexOf('/');
            if (slash > 0)
                localName = localName.Substring(slash + 1);

            string dir = Path.Combine(SkillsPath, localName);
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "SKILL.md");
            await File.WriteAllTextAsync(file, content);
            interactor.Poke($"喵，成功安装 Skill「{localName}」，位置：{file}");
        }
        catch (Exception ex)
        {
            interactor.Poke($"喵，安装 Skill「{skillName}」失败：{ex.Message}");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("列出本地已安装的 Skill（Skills 目录下含有 SKILL.md 的子目录名）")]
    public Task ListLocalSkills()
    {
        try
        {
            var names = new List<string>();
            if (Directory.Exists(SkillsPath))
            {
                foreach (string dir in Directory.GetDirectories(SkillsPath))
                {
                    if (File.Exists(Path.Combine(dir, "SKILL.md")))
                    {
                        string? name = Path.GetFileName(dir);
                        if (!string.IsNullOrEmpty(name))
                            names.Add(name);
                    }
                }
            }

            if (names.Count == 0)
            {
                interactor.Poke("喵，本地还没有安装任何 Skill。");
                return Task.CompletedTask;
            }
            interactor.Poke("喵，本地已安装的 Skill：\n- " + string.Join("\n- ", names));
        }
        catch (Exception ex)
        {
            interactor.Poke($"喵，读取本地 Skill 失败：{ex.Message}");
        }
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("返回当前选择的市场源和自定义市场源")]
    public Task GetMarketSources()
    {
        interactor.Poke($"喵，当前市场源：{Configuration?.Sources ?? ""}；自定义市场源：{Configuration?.CustomSources ?? ""}");
        return Task.CompletedTask;
    }

    public static async Task<List<SkillInfo>> FetchSkills(string source)
    {
        SkillSource src = ParseSource(source);
        if (src.Provider == ProviderKind.ModelScopeSkills)
            return await FetchModelScopeSkills();

        List<string> names = await FetchSkillNames(source);
        string repoUrl = BuildRepoWebUrl(src);
        var result = new List<SkillInfo>();
        foreach (string name in names)
        {
            string content = "";
            string description = "";
            try
            {
                string doc = await FetchSkillDoc(source, name);
                description = ExtractDescription(doc);
                content = StripFrontmatter(doc);
            }
            catch
            {
                // 单个 Skill 的详情获取失败不影响整体列表展示
            }
            result.Add(new SkillInfo { Name = name, Description = description, Content = content, Url = repoUrl });
        }
        return result;
    }

    public static string ExtractDescription(string skillMd)
    {
        if (string.IsNullOrWhiteSpace(skillMd))
            return "";

        string text = skillMd.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = text.Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
            return "";

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Trim() == "---")
                break;

            int colon = line.IndexOf(':');
            if (colon <= 0)
                continue;

            string key = line.Substring(0, colon).Trim();
            if (key.Equals("description", StringComparison.OrdinalIgnoreCase))
            {
                string value = line.Substring(colon + 1).Trim();
                return StripQuotes(value);
            }
        }
        return "";
    }

    private static string StripQuotes(string s)
    {
        if (s.Length >= 2)
        {
            char first = s[0];
            char last = s[s.Length - 1];
            if ((first == '"' && last == '"') ||
                (first == '\'' && last == '\'') ||
                (first == '“' && last == '”'))
            {
                return s.Substring(1, s.Length - 2);
            }
        }
        return s;
    }

    public static string StripFrontmatter(string skillMd)
    {
        if (string.IsNullOrWhiteSpace(skillMd))
            return "";

        string text = skillMd.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = text.Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
            return skillMd;

        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                return string.Join("\n", lines, i + 1, lines.Length - (i + 1)).Trim();
            }
        }
        return skillMd;
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Alife-SkillStore");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public static async Task<string> TranslateTextAsync(string text, SkillStoreConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(text) || cfg == null)
            return text;
        if (IsMostlyChinese(text))
            return text; // 已是中文，无需翻译

        // 长文本分块翻译（免费/付费接口都有单次长度限制）
        const int chunkSize = 1500;
        if (text.Length > chunkSize)
        {
            var sb = new StringBuilder(text.Length + 64);
            foreach (string chunk in SplitChunks(text, chunkSize))
                sb.Append(await TranslateOnceAsync(chunk, cfg));
            return sb.ToString();
        }
        return await TranslateOnceAsync(text, cfg);
    }

    private static async Task<string> TranslateOnceAsync(string text, SkillStoreConfig cfg)
    {
        string provider = (cfg.TranslationProvider ?? "").Trim().ToLowerInvariant();
        using var http = CreateHttp();

        return provider switch
        {
            "baidu" => await TranslateBaiduAsync(http, text, cfg),
            "youdao" => await TranslateYoudaoAsync(http, text, cfg),
            "deepl" => await TranslateDeepLAsync(http, text, cfg),
            "google" => await TranslateGoogleAsync(http, text),
            "custom" => await TranslateCustomAsync(http, text, cfg),
            _ => await TranslateMyMemoryAsync(http, text)
        };
    }

    private static List<string> SplitChunks(string text, int maxChars)
    {
        var chunks = new List<string>();
        int start = 0;
        while (start < text.Length)
        {
            int end = Math.Min(start + maxChars, text.Length);
            if (end < text.Length)
            {
                int nl = text.LastIndexOf('\n', end - 1);
                if (nl > start)
                    end = nl + 1;
            }
            chunks.Add(text.Substring(start, end - start));
            start = end;
        }
        return chunks;
    }

    public static bool IsMostlyChinese(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        int letters = 0;
        int han = 0;
        foreach (char c in text)
        {
            if (char.IsLetter(c))
            {
                letters++;
                if (c >= 0x4E00 && c <= 0x9FFF)
                    han++;
            }
        }
        return letters > 0 && han * 2 >= letters;
    }

    private static async Task<string> TranslateMyMemoryAsync(HttpClient http, string text)
    {
        string url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(text) + "&langpair=en|zh-CN";
        EnsureSafeHost(url);
        string json = await http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        string result = GetJsonPath(doc.RootElement, "responseData.translatedText");
        if (string.IsNullOrWhiteSpace(result))
            throw new Exception("翻译无结果");
        if (result.StartsWith("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase) ||
            result.StartsWith("QUERY LENGTH LIMIT", StringComparison.OrdinalIgnoreCase) ||
            result.StartsWith("NO QUERY", StringComparison.OrdinalIgnoreCase))
            throw new Exception("MyMemory 免费额度已用完或请求无效，请换翻译服务或稍后再试");
        return result;
    }

    private static async Task<string> TranslateGoogleAsync(HttpClient http, string text)
    {
        string url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl=zh-CN&dt=t&q=" + Uri.EscapeDataString(text);
        EnsureSafeHost(url);
        string json = await http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Array &&
            doc.RootElement.GetArrayLength() > 0 &&
            doc.RootElement[0].ValueKind == JsonValueKind.Array &&
            doc.RootElement[0].GetArrayLength() > 0 &&
            doc.RootElement[0][0].ValueKind == JsonValueKind.Array &&
            doc.RootElement[0][0].GetArrayLength() > 0 &&
            doc.RootElement[0][0][0].ValueKind == JsonValueKind.String)
        {
            string result = doc.RootElement[0][0][0].GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(result))
                return result;
        }
        throw new Exception("谷歌翻译响应格式异常");
    }

    private static async Task<string> TranslateBaiduAsync(HttpClient http, string text, SkillStoreConfig cfg)
    {
        string appid = (cfg.TranslationApiKey ?? "").Trim();
        string secret = (cfg.TranslationApiSecret ?? "").Trim();
        if (appid.Length == 0 || secret.Length == 0)
            throw new ArgumentException("百度翻译需要 appid 和 secret");

        string salt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        string sign = Md5Hex(appid + text + salt + secret);
        string url = "https://api.fanyi.baidu.com/api/trans/vip/translate?q=" + Uri.EscapeDataString(text) +
                     "&from=en&to=zh&appid=" + Uri.EscapeDataString(appid) +
                     "&salt=" + salt + "&sign=" + sign;
        EnsureSafeHost(url);
        string json = await http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("trans_result", out var arr) &&
            arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0 &&
            arr[0].TryGetProperty("dst", out var dst) && dst.ValueKind == JsonValueKind.String)
        {
            string result = dst.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(result))
                return result;
        }
        throw new Exception("百度翻译响应无结果");
    }

    private static async Task<string> TranslateYoudaoAsync(HttpClient http, string text, SkillStoreConfig cfg)
    {
        string appKey = (cfg.TranslationApiKey ?? "").Trim();
        string appSecret = (cfg.TranslationApiSecret ?? "").Trim();
        if (appKey.Length == 0 || appSecret.Length == 0)
            throw new ArgumentException("有道智云需要 appKey 和 appSecret");

        string salt = Guid.NewGuid().ToString("N");
        string curtime = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        string input = text.Length > 20
            ? text.Substring(0, 10) + text.Length + text.Substring(text.Length - 10)
            : text;
        string sign = Sha256Hex(appKey + input + salt + curtime + appSecret);

        string url = "https://openapi.youdao.com/api";
        EnsureSafeHost(url);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["q"] = text,
            ["from"] = "auto",
            ["to"] = "zh-CHS",
            ["appKey"] = appKey,
            ["salt"] = salt,
            ["sign"] = sign,
            ["signType"] = "v3",
            ["curtime"] = curtime
        });
        HttpResponseMessage resp = await http.PostAsync(url, content);
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("translation", out var arr) &&
            arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0 &&
            arr[0].ValueKind == JsonValueKind.String)
        {
            string result = arr[0].GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(result))
                return result;
        }
        throw new Exception("有道翻译响应无结果");
    }

    private static async Task<string> TranslateDeepLAsync(HttpClient http, string text, SkillStoreConfig cfg)
    {
        string key = (cfg.TranslationApiKey ?? "").Trim();
        if (key.Length == 0)
            throw new ArgumentException("DeepL 需要 API Key");

        string url = "https://api-free.deepl.com/v2/translate";
        EnsureSafeHost(url);
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + key);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["text"] = text,
            ["source_lang"] = "EN",
            ["target_lang"] = "ZH"
        });
        HttpResponseMessage resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("translations", out var arr) &&
            arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0 &&
            arr[0].TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
        {
            string result = t.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(result))
                return result;
        }
        throw new Exception("DeepL 响应无结果");
    }

    private static async Task<string> TranslateCustomAsync(HttpClient http, string text, SkillStoreConfig cfg)
    {
        string url = (cfg.TranslationApi ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("自定义翻译接口未配置");

        url = url.Replace("{text}", Uri.EscapeDataString(text));
        EnsureSafeHost(url);
        string json = await http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        return GetJsonPath(doc.RootElement, cfg.TranslationResultPath);
    }

    private static string GetJsonPath(JsonElement el, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        JsonElement cur = el;
        foreach (string seg in path.Split('.'))
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(seg, out cur))
                return "";
        }
        if (cur.ValueKind == JsonValueKind.String)
            return cur.GetString() ?? "";
        return "";
    }

    private static string Md5Hex(string input)
    {
        // 百度翻译接口签名协议规定 sign=MD5(appid+q+salt+secret)，属于外部接口契约，并非安全用途
        using var md5 = System.Security.Cryptography.MD5.Create();
        byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static string Sha256Hex(string input)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    public static async Task<List<string>> FetchSkillNames(string source)
    {
        SkillSource src = ParseSource(source);
        using var http = CreateHttp();
        return src.Provider switch
        {
            ProviderKind.GitLab => await FetchGitLabSkillNames(http, src),
            ProviderKind.ModelScope => await FetchModelScopeRepoSkillNames(http, src),
            ProviderKind.ModelScopeSkills => await FetchModelScopeSkillsNames(http),
            _ => await FetchGitHubLikeSkillNames(http, src)
        };
    }

    public static async Task<string> FetchSkillDoc(string source, string skillName)
    {
        SkillSource src = ParseSource(source);
        using var http = CreateHttp();
        if (src.Provider == ProviderKind.ModelScopeSkills)
            return await FetchModelScopeSkillDocByKey(http, skillName);
        return src.Provider switch
        {
            ProviderKind.GitLab => await FetchGitLabSkillDoc(http, src, skillName),
            ProviderKind.ModelScope => await FetchModelScopeRepoSkillDoc(http, src, skillName),
            _ => await FetchGitHubLikeSkillDoc(http, src, skillName)
        };
    }

    private static async Task<List<string>> FetchGitHubLikeSkillNames(HttpClient http, SkillSource src)
    {
        string url = $"{src.ApiBase}/repos/{src.Owner}/{src.Repo}/contents/skills";
        EnsureSafeHost(url);
        string json = await http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        var names = new List<string>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return names;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            string? type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "dir" && item.TryGetProperty("name", out var name))
            {
                string? n = name.GetString();
                if (!string.IsNullOrEmpty(n))
                    names.Add(n);
            }
        }
        return names;
    }

    private static async Task<string> FetchGitHubLikeSkillDoc(HttpClient http, SkillSource src, string skillName)
    {
        string url = $"{src.ApiBase}/repos/{src.Owner}/{src.Repo}/contents/skills/{skillName}/SKILL.md";
        EnsureSafeHost(url);
        string json = await http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
        {
            string base64 = contentEl.GetString() ?? "";
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64.Replace("\n", "")));
        }
        throw new Exception("响应中没有 SKILL.md 内容");
    }

    private static async Task<List<string>> FetchGitLabSkillNames(HttpClient http, SkillSource src)
    {
        string encodedProject = Uri.EscapeDataString(src.Owner);
        string url = $"{src.ApiBase}/projects/{encodedProject}/repository/tree?path=skills&per_page=100";
        EnsureSafeHost(url);
        string json = await http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        var names = new List<string>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            string? type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "tree" && item.TryGetProperty("name", out var name))
            {
                string? n = name.GetString();
                if (!string.IsNullOrEmpty(n))
                    names.Add(n);
            }
        }
        return names;
    }

    private static async Task<string> FetchGitLabSkillDoc(HttpClient http, SkillSource src, string skillName)
    {
        string encodedProject = Uri.EscapeDataString(src.Owner);
        string encodedFile = Uri.EscapeDataString($"skills/{skillName}/SKILL.md");
        string url = $"{src.ApiBase}/projects/{encodedProject}/repository/files/{encodedFile}/raw";
        EnsureSafeHost(url);
        byte[] bytes = await http.GetByteArrayAsync(url);
        return Encoding.UTF8.GetString(bytes);
    }

    private static async Task<List<string>> FetchModelScopeRepoSkillNames(HttpClient http, SkillSource src)
    {
        string url = $"https://modelscope.cn/api/v1/models/{src.Owner}/{src.Repo}/repo/files?Revision=master&Root=skills";
        EnsureSafeHost(url);
        string json = await http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        var names = new List<string>();
        if (doc.RootElement.TryGetProperty("Data", out var data) &&
            data.TryGetProperty("Files", out var files))
        {
            foreach (var item in files.EnumerateArray())
            {
                string? type = item.TryGetProperty("Type", out var t) ? t.GetString() : null;
                if (type == "tree" && item.TryGetProperty("Name", out var name))
                {
                    string? n = name.GetString();
                    if (!string.IsNullOrEmpty(n))
                        names.Add(n);
                }
            }
        }
        return names;
    }

    private static async Task<string> FetchModelScopeRepoSkillDoc(HttpClient http, SkillSource src, string skillName)
    {
        string url = $"https://modelscope.cn/api/v1/models/{src.Owner}/{src.Repo}/repo?Revision=master&FilePath=skills/{skillName}/SKILL.md";
        EnsureSafeHost(url);
        byte[] bytes = await http.GetByteArrayAsync(url);
        return Encoding.UTF8.GetString(bytes);
    }

    private const int MarketplacePageSize = 200;

    private static async Task<List<SkillInfo>> FetchModelScopeSkills()
    {
        using var http = CreateHttp();
        return await FetchModelScopeSkillListPage(http, 1, MarketplacePageSize);
    }

    public static async Task<List<SkillInfo>> FetchModelScopeSkillsPage(int pageNumber, int pageSize = MarketplacePageSize)
    {
        using var http = CreateHttp();
        return await FetchModelScopeSkillListPage(http, pageNumber, pageSize);
    }

    public static bool IsMarketplace(string source)
    {
        try
        {
            return ParseSource(source).Provider == ProviderKind.ModelScopeSkills;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<List<SkillInfo>> FetchModelScopeSkillListPage(HttpClient http, int pageNumber, int pageSize)
    {
        string url = "https://modelscope.cn/api/v1/dolphin/skills";
        EnsureSafeHost(url);

        var body = JsonSerializer.Serialize(new
        {
            PageSize = pageSize,
            PageNumber = pageNumber,
            Query = "",
            Sort = "Default",
            Criterion = new object[0],
            WithTopCollection = false
        });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        HttpResponseMessage resp = await http.PutAsync(url, content);
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);
        var result = new List<SkillInfo>();
        if (doc.RootElement.TryGetProperty("Data", out var data) &&
            data.TryGetProperty("SkillList", out var list))
        {
            foreach (var item in list.EnumerateArray())
            {
                string path = GetProp(item, "Path");
                string name = GetProp(item, "Name");
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                string displayName = GetProp(item, "DisplayName");
                if (string.IsNullOrWhiteSpace(displayName))
                    displayName = name;
                result.Add(new SkillInfo
                {
                    Name = name,
                    DisplayName = displayName,
                    Path = path,
                    Description = GetProp(item, "Description"),
                    Url = string.IsNullOrEmpty(path)
                        ? ""
                        : $"https://modelscope.cn/skills/{path}/{name}"
                });
            }
        }
        return result;
    }

    private static async Task<List<string>> FetchModelScopeSkillsNames(HttpClient http)
    {
        List<SkillInfo> items = await FetchModelScopeSkillListPage(http, 1, MarketplacePageSize);
        var names = new List<string>();
        foreach (SkillInfo info in items)
            names.Add($"{info.Path}/{info.Name}");
        return names;
    }

    private static async Task<string> FetchModelScopeSkillDocByKey(HttpClient http, string key)
    {
        string path = key;
        string name = "";
        int slash = key.IndexOf('/');
        if (slash > 0 && slash < key.Length - 1)
        {
            path = key.Substring(0, slash);
            name = key.Substring(slash + 1);
        }
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(name))
            throw new ArgumentException($"Skill 标识不正确：{key}（魔搭中心应为 Path/Name）");

        string url = $"https://modelscope.cn/api/v1/skills/{Uri.EscapeDataString(path)}/{Uri.EscapeDataString(name)}";
        EnsureSafeHost(url);
        string json = await http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("Data", out var data) &&
            data.TryGetProperty("ReadMeContent", out var rmc) &&
            rmc.ValueKind == JsonValueKind.String)
        {
            string c = rmc.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(c))
                return c;
        }
        throw new Exception("响应中没有 SKILL.md 内容");
    }

    private static string GetProp(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String)
            return p.GetString() ?? "";
        return "";
    }

    private static SkillSource ParseSource(string? source)
    {
        string s = (source ?? "").Trim();

        if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return ParseUrlSource(s);
        }

        ProviderKind provider = ProviderKind.GitHubLike;
        string apiBase = "https://api.github.com";

        if (s.StartsWith("modelscope:", StringComparison.OrdinalIgnoreCase))
        {
            s = s.Substring("modelscope:".Length).Trim();
            if (s.Equals("skills", StringComparison.OrdinalIgnoreCase))
                return new SkillSource { Provider = ProviderKind.ModelScopeSkills };
            provider = ProviderKind.ModelScope;
            apiBase = "";
        }
        else if (s.StartsWith("ms:", StringComparison.OrdinalIgnoreCase))
        {
            s = s.Substring(3).Trim();
            if (s.Equals("skills", StringComparison.OrdinalIgnoreCase))
                return new SkillSource { Provider = ProviderKind.ModelScopeSkills };
            provider = ProviderKind.ModelScope;
            apiBase = "";
        }
        else if (s.StartsWith("gitlab:", StringComparison.OrdinalIgnoreCase))
        {
            provider = ProviderKind.GitLab;
            apiBase = "https://gitlab.com/api/v4";
            s = s.Substring("gitlab:".Length);
        }
        else if (s.StartsWith("gitee:", StringComparison.OrdinalIgnoreCase))
        {
            provider = ProviderKind.GitHubLike;
            apiBase = "https://gitee.com/api/v5";
            s = s.Substring("gitee:".Length);
        }
        else if (s.StartsWith("codeberg:", StringComparison.OrdinalIgnoreCase))
        {
            provider = ProviderKind.GitHubLike;
            apiBase = "https://codeberg.org/api/v1";
            s = s.Substring("codeberg:".Length);
        }
        else if (s.StartsWith("gitea:", StringComparison.OrdinalIgnoreCase))
        {
            provider = ProviderKind.GitHubLike;
            apiBase = "https://gitea.com/api/v1";
            s = s.Substring("gitea:".Length);
        }
        else if (s.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
        {
            provider = ProviderKind.GitHubLike;
            apiBase = "https://api.github.com";
            s = s.Substring("github:".Length);
        }

        (string owner, string repo) = SplitOwnerRepo(s.Trim());
        return new SkillSource { Provider = provider, Owner = owner, Repo = repo, ApiBase = apiBase };
    }

    private static SkillSource ParseUrlSource(string url)
    {
        Uri uri = new Uri(url);
        string host = uri.Host.ToLowerInvariant();
        string path = uri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path.Substring(0, path.Length - 4);
        string[] segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (host == "modelscope.cn" || host == "www.modelscope.cn")
        {
            if (segs.Length >= 1 && segs[0].Equals("skills", StringComparison.OrdinalIgnoreCase))
            {
                return new SkillSource { Provider = ProviderKind.ModelScopeSkills };
            }
            int modelsIdx = Array.FindIndex(segs, x => x.Equals("models", StringComparison.OrdinalIgnoreCase));
            if (modelsIdx >= 0 && modelsIdx + 2 < segs.Length)
            {
                return new SkillSource
                {
                    Provider = ProviderKind.ModelScope,
                    Owner = segs[modelsIdx + 1],
                    Repo = segs[modelsIdx + 2],
                    ApiBase = ""
                };
            }
            throw new ArgumentException("魔搭地址应为 https://modelscope.cn/skills（Skill 中心）或 https://modelscope.cn/models/owner/repo");
        }

        if (host == "gitlab.com" || host == "www.gitlab.com" || host.Contains("gitlab"))
        {
            if (segs.Length < 2)
                throw new ArgumentException("GitLab URL 缺少 owner/repo 路径段");
            string repo = segs[segs.Length - 1];
            string owner = string.Join("/", segs, 0, segs.Length - 1);
            return new SkillSource
            {
                Provider = ProviderKind.GitLab,
                Owner = owner,
                Repo = repo,
                ApiBase = $"https://{host}/api/v4"
            };
        }

        (string o, string r) = FirstTwo(segs);

        string apiBase;
        if (host == "github.com" || host == "www.github.com")
            apiBase = "https://api.github.com";
        else if (host == "gitee.com" || host == "www.gitee.com")
            apiBase = "https://gitee.com/api/v5";
        else
            apiBase = uri.GetLeftPart(UriPartial.Authority) + "/api/v1";

        return new SkillSource
        {
            Provider = ProviderKind.GitHubLike,
            Owner = o,
            Repo = r,
            ApiBase = apiBase
        };
    }

    private static (string Owner, string Repo) SplitOwnerRepo(string path)
    {
        string p = path.Trim().TrimEnd('/');
        int slash = p.IndexOf('/');
        if (slash <= 0 || slash == p.Length - 1)
            throw new ArgumentException($"市场源格式不正确：「{path}」。GitHub 用 owner/repo，魔搭中心用 https://modelscope.cn/skills，其它网站请直接填仓库完整 URL。");
        return (p.Substring(0, slash).Trim(), p.Substring(slash + 1).Trim());
    }

    private static (string Owner, string Repo) FirstTwo(string[] segs)
    {
        if (segs.Length < 2)
            throw new ArgumentException("仓库 URL 缺少 owner/repo 路径段");
        return (segs[0], segs[1]);
    }

    private static string BuildRepoWebUrl(SkillSource src)
    {
        if (src.Provider == ProviderKind.ModelScope && src.Owner.Length > 0)
            return $"https://modelscope.cn/models/{src.Owner}/{src.Repo}";

        string apiBase = src.ApiBase;
        if (string.IsNullOrEmpty(apiBase))
            return "";

        if (apiBase == "https://api.github.com")
            return $"https://github.com/{src.Owner}/{src.Repo}";
        if (apiBase == "https://gitee.com/api/v5")
            return $"https://gitee.com/{src.Owner}/{src.Repo}";
        if (apiBase.EndsWith("/api/v4", StringComparison.OrdinalIgnoreCase))
            return apiBase.Substring(0, apiBase.Length - "/api/v4".Length) + "/" + src.Owner;
        if (apiBase.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
            return apiBase.Substring(0, apiBase.Length - "/api/v1".Length) + "/" + src.Owner + "/" + src.Repo;
        return "";
    }

    private static readonly HashSet<string> TrustedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.github.com", "github.com", "www.github.com",
        "gitee.com", "www.gitee.com",
        "gitlab.com", "www.gitlab.com",
        "modelscope.cn", "www.modelscope.cn",
        "codeberg.org", "gitea.com",
        "api.mymemory.translated.net",
        "translate.googleapis.com",
        "api.fanyi.baidu.com",
        "openapi.youdao.com",
        "api-free.deepl.com"
    };

    private static void EnsureSafeHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            throw new ArgumentException("无效的请求地址。");
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            throw new ArgumentException("仅允许 http/https 请求。");

        string host = uri.Host;
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("请求地址缺少主机名。");

        // 白名单域名是代码内置的公开站点，跳过 DNS 级校验，避免网络 DNS 污染导致误判；
        // 只有用户自定义的域名才做严格校验，防止 SSRF 到内网/回环/保留地址。
        if (TrustedHosts.Contains(host))
            return;

        IPAddress[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses(host);
        }
        catch
        {
            throw new ArgumentException($"无法解析主机名：{host}");
        }
        if (addresses.Length == 0)
            throw new ArgumentException($"无法解析主机名：{host}");

        foreach (IPAddress addr in addresses)
        {
            if (IsForbiddenAddress(addr))
                throw new ArgumentException($"拒绝访问不安全的主机地址：{host}");
        }
    }

    private static bool IsForbiddenAddress(IPAddress addr)
    {
        if (IPAddress.IsLoopback(addr))
            return true;
        if (addr.IsIPv6LinkLocal || addr.IsIPv6SiteLocal)
            return true;
        if (addr.Equals(IPAddress.IPv6Any) || addr.Equals(IPAddress.IPv6Loopback))
            return true;

        byte[] b = addr.GetAddressBytes();
        if (b.Length == 4)
        {
            byte a = b[0];
            if (a == 0) return true;                                       // 0.0.0.0/8
            if (a == 10) return true;                                      // 10.0.0.0/8
            if (a == 127) return true;                                     // 127.0.0.0/8
            if (a == 169 && b[1] == 254) return true;                      // 169.254.0.0/16 链路本地/云元数据
            if (a == 172 && b[1] >= 16 && b[1] <= 31) return true;         // 172.16.0.0/12
            if (a == 192 && b[1] == 168) return true;                      // 192.168.0.0/16
            if (a == 100 && b[1] >= 64 && b[1] <= 127) return true;        // 100.64.0.0/10 运营商级 NAT
            if (a >= 224) return true;                                     // 组播/保留
        }
        else if (b.Length == 16)
        {
            if ((b[0] & 0xFE) == 0xFC) return true;                        // fc00::/7 唯一本地
            if (b[0] == 0xFF) return true;                                 // ff00::/8 组播
        }
        return false;
    }
}
