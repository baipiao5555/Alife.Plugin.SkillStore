using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Framework;
using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;

namespace BDFFZI.MaoMao.SkillStore;

public sealed class SkillStoreUI : ModuleUIBase<SkillStoreModule, SkillStoreConfig>
{
    private static readonly string SkillsPath = Path.Combine(AlifePath.StorageFolderPath, "Skills");

    private static readonly (string Label, string Value)[] PresetSources =
    {
        ("魔搭 Skill 中心", "https://modelscope.cn/skills"),
        ("GitHub · anthropics/skills", "anthropics/skills"),
        ("GitHub · addyosmani/agent-skills", "addyosmani/agent-skills")
    };

    private static readonly (string Label, string Value)[] TranslationProviders =
    {
        ("免费 MyMemory", "mymemory"),
        ("谷歌翻译", "google"),
        ("百度翻译", "baidu"),
        ("有道智云", "youdao"),
        ("DeepL", "deepl"),
        ("自定义接口", "custom")
    };

    private readonly List<MarketSkill> _skills = new();
    private bool _loading;
    private bool _translate;
    private string _newSource = "";
    private string _status = "";
    private string _statusType = "";
    private string _translateNote = "";
    private int _marketPage;
    private bool _canLoadMore;

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        if (Configuration == null)
        {
            b.AddContent(0, "Configuration NULL");
            return;
        }

        int i = 0;
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "style",
            "padding:18px;display:flex;flex-direction:column;gap:4px;box-sizing:border-box;min-width:0;");

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "style", "font-size:16px;font-weight:700;margin-bottom:8px;");
        b.AddContent(i++, "Skill 商店");
        b.CloseElement();

        Hint(b, ref i,
            "从 GitHub、Gitee、GitLab、Codeberg/Gitea、魔搭等浏览并下载 Skill 到本地 Skills 目录。在下拉框选择要使用的市场源；输入框可添加预设里没有的源（如 gitee:xxx/yyy、仓库完整 URL），点「＋ 添加」加入下拉。");

        Label(b, ref i, "市场源（选择要使用的）");
        b.OpenElement(i++, "select");
        b.AddAttribute(i++, "style",
            "width:100%;box-sizing:border-box;padding:6px 9px;border:1px solid #d9d9d9;border-radius:6px;font-size:13px;");
        b.AddAttribute(i++, "onchange",
            EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
            {
                if (e.Value is string v && v.Length > 0)
                {
                    Configuration.Sources = v;
                    _ = InvokeAsync(StateHasChanged);
                }
            }));

        foreach (var preset in PresetSources)
        {
            b.OpenElement(i++, "option");
            b.AddAttribute(i++, "value", preset.Value);
            b.AddContent(i++, preset.Label);
            b.CloseElement();
        }

        List<string> customList = ParseSources(Configuration.CustomSources);
        foreach (string custom in customList)
        {
            b.OpenElement(i++, "option");
            b.AddAttribute(i++, "value", custom);
            b.AddContent(i++, custom);
            b.CloseElement();
        }

        b.CloseElement();

        if (customList.Count > 0)
        {
            for (int ci = 0; ci < customList.Count; ci++)
            {
                string custom = customList[ci];
                int captured = ci;
                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "style",
                    "display:flex;align-items:center;justify-content:space-between;gap:8px;padding:5px 8px;border:1px solid #e3e3e3;border-radius:6px;margin-bottom:4px;background:#fafafa;");
                b.OpenElement(i++, "span");
                b.AddAttribute(i++, "style", "font-size:12px;color:#888;word-break:break-all;");
                b.AddContent(i++, custom);
                b.CloseElement();
                b.OpenElement(i++, "button");
                b.AddAttribute(i++, "type", "button");
                b.AddAttribute(i++, "style",
                    "border:none;background:none;color:#ff4d4f;cursor:pointer;font-size:14px;padding:0 4px;");
                b.AddAttribute(i++, "onclick",
                    EventCallback.Factory.Create<MouseEventArgs>(this, _ => RemoveCustomSource(captured)));
                b.AddContent(i++, "✕");
                b.CloseElement();
                b.CloseElement();
            }
        }

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "style", "display:flex;gap:6px;align-items:center;margin-top:4px;");
        b.OpenElement(i++, "input");
        b.AddAttribute(i++, "type", "text");
        b.AddAttribute(i++, "value", _newSource);
        b.AddAttribute(i++, "style",
            "flex:1;min-width:0;box-sizing:border-box;padding:6px 9px;border:1px solid #d9d9d9;border-radius:6px;font-size:12px;");
        b.AddAttribute(i++, "onchange",
            EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
            {
                if (e.Value is string s)
                    _newSource = s;
            }));
        b.CloseElement();
        AddButton(b, ref i, "＋ 添加", () => { AddSource(); return Task.CompletedTask; });
        b.CloseElement();

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "style", "margin-top:8px;");
        AddButton(b, ref i, "刷新市场列表", RefreshAsync);
        b.CloseElement();

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "style",
            "margin-top:6px;display:flex;align-items:center;gap:8px;");
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "style", "font-size:12px;font-weight:600;");
        b.AddContent(i++, "翻译成中文（简介）");
        b.CloseElement();
        b.OpenComponent<Switch>(i++);
        b.AddAttribute(i++, "Checked", _translate);
        b.AddAttribute(i++, "CheckedChanged",
            EventCallback.Factory.Create<bool>(this, v =>
            {
                _translate = v;
                _ = InvokeAsync(StateHasChanged);
            }));
        b.CloseComponent();
        b.CloseElement();

        SectionTitle(b, ref i, "翻译设置");

        Label(b, ref i, "翻译服务");
        b.OpenElement(i++, "select");
        b.AddAttribute(i++, "style",
            "width:100%;box-sizing:border-box;padding:6px 9px;border:1px solid #d9d9d9;border-radius:6px;font-size:13px;");
        b.AddAttribute(i++, "onchange",
            EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
            {
                if (e.Value is string v && v.Length > 0)
                    Configuration.TranslationProvider = v;
            }));

        foreach (var p in TranslationProviders)
        {
            b.OpenElement(i++, "option");
            b.AddAttribute(i++, "value", p.Value);
            b.AddContent(i++, p.Label);
            b.CloseElement();
        }

        b.CloseElement();

        AddInput(b, ref i, "翻译 API Key（百度 appid / 有道 appKey / DeepL key）",
            Configuration.TranslationApiKey, v => Configuration.TranslationApiKey = v);
        AddInput(b, ref i, "翻译 API Secret（百度 secret / 有道 appSecret）",
            Configuration.TranslationApiSecret, v => Configuration.TranslationApiSecret = v);
        AddInput(b, ref i, "自定义翻译接口（{text} 为占位符）",
            Configuration.TranslationApi, v => Configuration.TranslationApi = v);
        AddInput(b, ref i, "自定义翻译结果字段（JSON 点号路径）",
            Configuration.TranslationResultPath, v => Configuration.TranslationResultPath = v);

        if (_loading)
        {
            Hint(b, ref i, "加载中…");
        }

        if (_skills.Count > 0)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "style", "margin-top:6px;");
            foreach (var skill in _skills)
            {
                MarketSkill s = skill;
                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "style",
                    "border:1px solid #e3e3e3;border-radius:8px;margin-bottom:8px;background:#fff;padding:8px 10px;");

                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "style",
                    "display:flex;align-items:center;justify-content:space-between;gap:8px;");
                string title = string.IsNullOrWhiteSpace(s.DisplayName) ? s.Name : s.DisplayName;
                if (!string.IsNullOrWhiteSpace(s.Url))
                {
                    b.OpenElement(i++, "a");
                    b.AddAttribute(i++, "href", s.Url);
                    b.AddAttribute(i++, "target", "_blank");
                    b.AddAttribute(i++, "rel", "noopener");
                    b.AddAttribute(i++, "style",
                        "font-size:13px;font-weight:600;color:#1677ff;text-decoration:none;word-break:break-all;");
                    b.AddContent(i++, title);
                    b.CloseElement();
                }
                else
                {
                    b.OpenElement(i++, "span");
                    b.AddAttribute(i++, "style", "font-size:13px;font-weight:600;");
                    b.AddContent(i++, title);
                    b.CloseElement();
                }
                AddButton(b, ref i, "安装", () => InstallAsync(s));
                b.CloseElement();

                if (!string.IsNullOrWhiteSpace(s.Description))
                {
                    b.OpenElement(i++, "div");
                    b.AddAttribute(i++, "style",
                        "margin-top:4px;font-size:12px;color:#666;line-height:1.5;");
                    b.AddContent(i++, s.Description);
                    b.CloseElement();
                }

                if (!string.IsNullOrWhiteSpace(s.Content))
                {
                    b.OpenElement(i++, "details");
                    b.AddAttribute(i++, "style", "margin-top:6px;");

                    b.OpenElement(i++, "summary");
                    b.AddAttribute(i++, "style",
                        "cursor:pointer;font-size:12px;color:#1677ff;list-style:none;");
                    b.AddContent(i++, "查看详细说明");
                    b.CloseElement();

                    string body = string.IsNullOrWhiteSpace(s.TranslatedContent)
                        ? s.Content
                        : s.TranslatedContent;

                    b.OpenElement(i++, "div");
                    b.AddAttribute(i++, "style",
                        "margin-top:6px;white-space:pre-wrap;font-size:12px;color:#333;line-height:1.6;max-height:420px;overflow:auto;background:#fafafa;border:1px solid #eee;border-radius:6px;padding:8px 10px;");
                    b.AddContent(i++, body);
                    b.CloseElement();

                    b.OpenElement(i++, "div");
                    b.AddAttribute(i++, "style", "margin-top:6px;");
                    if (string.IsNullOrWhiteSpace(s.TranslatedContent))
                    {
                        if (s.Translating)
                        {
                            b.OpenElement(i++, "span");
                            b.AddAttribute(i++, "style", "font-size:12px;color:#888;");
                            b.AddContent(i++, "翻译中…");
                            b.CloseElement();
                        }
                        else
                        {
                            AddButton(b, ref i, "翻译全文", () => TranslateContentAsync(s));
                        }
                    }
                    b.CloseElement();

                    b.CloseElement();
                }
                else if (!string.IsNullOrWhiteSpace(s.Path))
                {
                    b.OpenElement(i++, "div");
                    b.AddAttribute(i++, "style", "margin-top:6px;");
                    if (s.LoadingContent)
                    {
                        b.OpenElement(i++, "span");
                        b.AddAttribute(i++, "style", "font-size:12px;color:#888;");
                        b.AddContent(i++, "加载中…");
                        b.CloseElement();
                    }
                    else
                    {
                        AddButton(b, ref i, "加载详细说明", () => LoadContentAsync(s));
                    }
                    b.CloseElement();
                }

                b.CloseElement();
            }
            b.CloseElement();
        }

        if (_canLoadMore)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "style", "margin-top:6px;");
            AddButton(b, ref i, "加载更多（第 " + (_marketPage + 1) + " 页）", LoadMoreAsync);
            b.CloseElement();
        }

        if (!string.IsNullOrEmpty(_status))
        {
            string color, bg, border;
            if (_statusType == "err")
            {
                color = "#ff4d4f"; bg = "#fff1f0"; border = "#ffa39e";
            }
            else if (_statusType == "warn")
            {
                color = "#d46b08"; bg = "#fff7e6"; border = "#ffd591";
            }
            else
            {
                color = "#52c41a"; bg = "#f6ffed"; border = "#b7eb8f";
            }

            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "style",
                $"margin-top:6px;padding:8px 10px;border-radius:8px;font-size:12px;color:{color};background:{bg};border:1px solid {border};white-space:pre-line;");
            b.AddContent(i++, _status);
            b.CloseElement();
        }

        b.CloseElement();
    }

    private async Task RefreshAsync()
    {
        if (_loading || Configuration == null)
            return;

        _loading = true;
        _status = "";
        _statusType = "";
        _translateNote = "";
        _marketPage = 1;
        _canLoadMore = false;
        _skills.Clear();
        await InvokeAsync(StateHasChanged);

        try
        {
            List<string> sources = ParseSources(Configuration.Sources);
            if (sources.Count == 0)
            {
                _status = "请先填写至少一个市场源。GitHub 用 owner/repo；魔搭中心用 https://modelscope.cn/skills。";
                _statusType = "err";
                return;
            }

            var errors = new List<string>();
            foreach (string source in sources)
            {
                try
                {
                    List<SkillInfo> skills = await SkillStoreModule.FetchSkills(source);
                    if (SkillStoreModule.IsMarketplace(source))
                    {
                        _marketPage = 1;
                        _canLoadMore = skills.Count >= 200;
                    }
                    foreach (SkillInfo info in skills)
                        _skills.Add(new MarketSkill
                        {
                            Source = source,
                            Name = info.Name,
                            DisplayName = info.DisplayName,
                            Path = info.Path,
                            Description = info.Description,
                            Content = info.Content,
                            Url = info.Url
                        });
                }
                catch (Exception ex)
                {
                    errors.Add($"{source}：{ex.Message}");
                }
            }

            if (_translate)
            {
                int translated = 0;
                int failed = 0;
                int skipped = 0;
                foreach (MarketSkill skill in _skills)
                {
                    if (string.IsNullOrWhiteSpace(skill.Description))
                        continue;
                    if (SkillStoreModule.IsMostlyChinese(skill.Description))
                    {
                        skipped++;
                        continue;
                    }
                    try
                    {
                        string before = skill.Description;
                        skill.Description = await SkillStoreModule.TranslateTextAsync(skill.Description, Configuration);
                        if (string.Equals(skill.Description, before, StringComparison.Ordinal))
                            failed++;
                        else
                            translated++;
                    }
                    catch
                    {
                        failed++;
                    }
                }
                _translateNote = $"翻译：成功 {translated}，失败 {failed}，已是中文跳过 {skipped}";
            }

            if (_skills.Count > 0)
            {
                _status = $"共找到 {_skills.Count} 个 Skill。";
                _statusType = errors.Count > 0 ? "warn" : "ok";
                if (errors.Count > 0)
                    _status += "\n部分源失败：" + string.Join("；", errors);
            }
            else if (errors.Count > 0)
            {
                _status = "获取失败：\n" + string.Join("\n", errors);
                _statusType = "err";
            }
            else
            {
                _status = "这些市场源里没有找到 Skill。";
                _statusType = "ok";
            }

            if (!string.IsNullOrEmpty(_translateNote))
                _status = _status + "\n" + _translateNote;
        }
        catch (Exception ex)
        {
            _status = $"刷新失败：{ex.Message}";
            _statusType = "err";
        }
        finally
        {
            _loading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task InstallAsync(MarketSkill skill)
    {
        if (_loading || Configuration == null)
            return;

        _loading = true;
        _status = "";
        _statusType = "";
        await InvokeAsync(StateHasChanged);

        try
        {
            string key = string.IsNullOrEmpty(skill.Path)
                ? skill.Name
                : $"{skill.Path}/{skill.Name}";
            string content = await SkillStoreModule.FetchSkillDoc(skill.Source, key);
            string dir = Path.Combine(SkillsPath, skill.Name);
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "SKILL.md");
            await File.WriteAllTextAsync(file, content);
            _status = $"✔ 已安装「{skill.Name}」到 {file}";
            _statusType = "ok";
        }
        catch (Exception ex)
        {
            _status = $"✖ 安装「{skill.Name}」失败：{ex.Message}";
            _statusType = "err";
        }
        finally
        {
            _loading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private void AddSource()
    {
        if (Configuration == null)
            return;

        string v = (_newSource ?? "").Trim();
        if (v.Length == 0)
            return;

        var customs = ParseSources(Configuration.CustomSources);
        if (!customs.Contains(v))
            customs.Add(v);
        Configuration.CustomSources = string.Join(",", customs);
        Configuration.Sources = v; // 自动选中刚添加的源
        _newSource = "";
        _ = InvokeAsync(StateHasChanged);
    }

    private void RemoveCustomSource(int index)
    {
        if (Configuration == null)
            return;

        var customs = ParseSources(Configuration.CustomSources);
        if (index >= 0 && index < customs.Count)
        {
            string removed = customs[index];
            customs.RemoveAt(index);
            Configuration.CustomSources = string.Join(",", customs);
            if (Configuration.Sources == removed)
                Configuration.Sources = PresetSources[0].Value;
        }
        _ = InvokeAsync(StateHasChanged);
    }

    private async Task TranslateContentAsync(MarketSkill s)
    {
        if (s.Translating || Configuration == null)
            return;

        if (SkillStoreModule.IsMostlyChinese(s.Content))
        {
            s.TranslatedContent = s.Content;
            _status = "内容已是中文，无需翻译。";
            _statusType = "ok";
            await InvokeAsync(StateHasChanged);
            return;
        }

        s.Translating = true;
        _status = "";
        _statusType = "";
        await InvokeAsync(StateHasChanged);

        try
        {
            s.TranslatedContent = await SkillStoreModule.TranslateTextAsync(s.Content, Configuration);
            if (string.IsNullOrWhiteSpace(s.TranslatedContent) ||
                string.Equals(s.TranslatedContent, s.Content, StringComparison.Ordinal))
            {
                s.TranslatedContent = "";
                _status = "翻译失败：翻译服务未返回译文（检查「翻译设置」里的服务和额度）。";
                _statusType = "err";
            }
            else
            {
                _status = "✔ 已翻译为中文。";
                _statusType = "ok";
            }
        }
        catch (Exception ex)
        {
            s.TranslatedContent = "";
            _status = $"翻译失败：{ex.Message}";
            _statusType = "err";
        }
        finally
        {
            s.Translating = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadContentAsync(MarketSkill s)
    {
        if (s.LoadingContent || Configuration == null)
            return;

        s.LoadingContent = true;
        await InvokeAsync(StateHasChanged);

        try
        {
            string key = string.IsNullOrEmpty(s.Path) ? s.Name : $"{s.Path}/{s.Name}";
            s.Content = await SkillStoreModule.FetchSkillDoc(s.Source, key);
        }
        catch
        {
            s.Content = "";
        }
        finally
        {
            s.LoadingContent = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadMoreAsync()
    {
        if (_loading || Configuration == null)
            return;

        _loading = true;
        _status = "";
        _statusType = "";
        _translateNote = "";
        await InvokeAsync(StateHasChanged);

        try
        {
            int next = _marketPage + 1;
            List<SkillInfo> more = await SkillStoreModule.FetchModelScopeSkillsPage(next);

            if (_translate)
            {
                int translated = 0;
                int failed = 0;
                int skipped = 0;
                foreach (SkillInfo info in more)
                {
                    if (string.IsNullOrWhiteSpace(info.Description))
                        continue;
                    if (SkillStoreModule.IsMostlyChinese(info.Description))
                    {
                        skipped++;
                        continue;
                    }
                    try
                    {
                        string before = info.Description;
                        info.Description = await SkillStoreModule.TranslateTextAsync(info.Description, Configuration);
                        if (string.Equals(info.Description, before, StringComparison.Ordinal))
                            failed++;
                        else
                            translated++;
                    }
                    catch
                    {
                        failed++;
                    }
                }
                _translateNote = $"翻译：成功 {translated}，失败 {failed}，已是中文跳过 {skipped}";
            }

            foreach (SkillInfo info in more)
                _skills.Add(new MarketSkill
                {
                    Source = Configuration.Sources,
                    Name = info.Name,
                    DisplayName = info.DisplayName,
                    Path = info.Path,
                    Description = info.Description,
                    Content = info.Content,
                    Url = info.Url
                });
            _marketPage = next;
            _canLoadMore = more.Count >= 200;

            _status = $"已加载到第 {_marketPage} 页，共 {_skills.Count} 个 Skill。";
            _statusType = "ok";
            if (!string.IsNullOrEmpty(_translateNote))
                _status = _status + "\n" + _translateNote;
        }
        catch (Exception ex)
        {
            _status = $"加载更多失败：{ex.Message}";
            _statusType = "err";
        }
        finally
        {
            _loading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private static List<string> ParseSources(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        foreach (string part in text.Split(','))
        {
            string s = part.Trim();
            if (s.Length > 0)
                result.Add(s);
        }
        return result;
    }

    private void AddButton(RenderTreeBuilder b, ref int seq, string text, Func<Task> action)
    {
        b.OpenElement(seq++, "button");
        b.AddAttribute(seq++, "type", "button");
        b.AddAttribute(seq++, "style",
            "padding:5px 12px;border:1px solid #1677ff;border-radius:6px;background:#fff;color:#1677ff;cursor:pointer;font-size:12px;");
        b.AddAttribute(seq++, "onclick",
            EventCallback.Factory.Create<MouseEventArgs>(this, _ => action()));
        b.AddContent(seq++, text);
        b.CloseElement();
    }

    private static void SectionTitle(RenderTreeBuilder b, ref int seq, string text)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "style",
            "font-size:13px;font-weight:700;color:#555;margin:14px 0 6px;border-bottom:1px solid #eee;padding-bottom:4px;");
        b.AddContent(seq++, text);
        b.CloseElement();
    }

    private void AddInput(RenderTreeBuilder b, ref int seq, string label, string value, Action<string> setter)
    {
        Label(b, ref seq, label);
        b.OpenElement(seq++, "input");
        b.AddAttribute(seq++, "type", "text");
        b.AddAttribute(seq++, "value", value ?? "");
        b.AddAttribute(seq++, "style",
            "width:100%;box-sizing:border-box;padding:6px 9px;border:1px solid #d9d9d9;border-radius:6px;font-size:12px;");
        b.AddAttribute(seq++, "onchange",
            EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
            {
                if (e.Value is string s)
                    setter(s);
            }));
        b.CloseElement();
    }

    private static void Label(RenderTreeBuilder b, ref int seq, string text)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "style", "font-size:12px;font-weight:600;margin:2px 0 4px;color:#333;");
        b.AddContent(seq++, text);
        b.CloseElement();
    }

    private static void Hint(RenderTreeBuilder b, ref int seq, string text)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "style", "font-size:12px;color:#888;line-height:1.6;");
        b.AddContent(seq++, text);
        b.CloseElement();
    }

    private sealed class MarketSkill
    {
        public string Source = "";
        public string Name = "";
        public string DisplayName = "";
        public string Path = "";
        public string Description = "";
        public string Content = "";
        public string Url = "";
        public string TranslatedContent = "";
        public bool Translating;
        public bool LoadingContent;
    }
}
