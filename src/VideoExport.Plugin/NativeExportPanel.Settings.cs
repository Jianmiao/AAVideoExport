using System.Globalization;
using AAVideoExport.Core;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace AAVideoExport.Plugin;

public sealed partial class NativeExportPanel
{
    private readonly ExportSettingsNavigation _settingsNavigation = new();
    private int _panelHeight = ExportPanelLayout.DefaultPanelHeight;
    private ExportPanelLayout? _settingsGeometry;
    private GameObject? _outputSection, _customDimensionsSection, _superSection, _superControls, _sharpnessSection;
    private GameObject? _audioSection, _advancedSection, _advancedEntry, _customBitrateSection, _footerSection;
    private GameObject? _settingsContent, _settingsScrollRail, _scrollDownButton;
    private UIPanel? _settingsViewport;
    private UITexture? _scrollTrack, _scrollThumb;
    private UITexture? _paperSurface, _outerShadow, _panelShadow, _outputCard, _superCard;
    private UISlider? _sharpnessSlider;
    private readonly List<BoxCollider> _settingsSliderColliders = new();

    private void BuildSettingsViewport(Transform parent)
    {
        var viewport = Child(parent, "Settings content viewport");
        _settingsViewport = viewport.AddComponent<UIPanel>();
        _settingsViewport.depth = _root!.GetComponent<UIPanel>().depth + 10;
        _settingsViewport.clipping = UIDrawCall.Clipping.SoftClip;
        _settingsViewport.clipSoftness = Vector2.zero;
        _settingsContent = Child(viewport.transform, "Scrollable settings content");

        // Only this narrow rail and the central content move. The title, paths,
        // summary and export button are outside the clipping panel.
        _settingsScrollRail = Child(parent, "Settings scroll controls");
        var rail = _settingsScrollRail.transform;
        Button(rail, 952, 0, 20, 24, () => "▴", () => ScrollSettings(-64),
            enabled: () => _settingsNavigation.ScrollOffset > 0);
        _scrollDownButton = Child(rail, "Scroll down");
        Button(_scrollDownButton.transform, 952, 0, 20, 24, () => "▾", () => ScrollSettings(64),
            enabled: () => _settingsGeometry != null && _settingsNavigation.ScrollOffset < _settingsGeometry.MaximumScroll);
        _scrollTrack = Rounded(rail, "Scroll track", 959, 32, 6, 80, 3, Line, 20);
        _scrollThumb = Rounded(rail, "Scroll position", 959, 32, 6, 28, 3, Muted, 21);
    }

    private void RefreshSettingsLayout()
    {
        if (_settingsRoot == null || _outputSection == null || _nativeRoot == null) return;
        float activeHeight = Math.Max(1, _nativeRoot.activeHeight);
        float activeWidth = activeHeight * Screen.width / Math.Max(1, (float)Screen.height);
        int shellHeight = ExportPanelLayout.HeightForViewport(activeWidth, activeHeight);
        var layout = ExportPanelLayout.Create(_preset == "custom", _options.SuperResolutionEnabled,
            _options.UpscaleAlgorithm == "anime4k-rcas", _settingsNavigation.IsAdvanced, !_recommendBitrate, shellHeight);
        if (layout == _settingsGeometry && _panelHeight == (_busy ? Height : layout.PanelHeight)) return;
        if (layout != _settingsGeometry)
        {
            // Commit focused text before hiding any page/conditional row. Invalid
            // pending text is retained too, and is validated by RequestExport.
            PollInputs();
            CloseDropdown();
            ReleaseInputFocus();
            PlaceSection(_outputSection, layout.OutputY);
            PlaceSection(_superSection, layout.SuperResolutionY);
            PlaceSection(_audioSection, layout.AudioY);
            PlaceSection(_advancedEntry, layout.AdvancedY);
            PlaceSection(_advancedSection, layout.AdvancedBodyY);
            PlaceSection(_footerSection, layout.FooterY);
            _advancedSection?.SetActive(layout.ShowAdvanced);
            _customDimensionsSection?.SetActive(layout.ShowCustomDimensions);
            _superControls?.SetActive(layout.ShowSuperResolutionControls);
            _sharpnessSection?.SetActive(layout.ShowSharpness);
            if (_outputCard != null) _outputCard.height = layout.OutputHeight;
            if (_superCard != null) _superCard.height = layout.SuperResolutionHeight;
            _settingsGeometry = layout;
            _settingsNavigation.ScrollTo(_settingsNavigation.ScrollOffset, layout);
            RefreshSettingsScroll();
        }
        int height = _busy ? Height : layout.PanelHeight;
        if (_panelHeight != height) CloseDropdown();
        _panelHeight = height;
        if (_paperSurface != null) _paperSurface.height = height;
        if (_outerShadow != null) _outerShadow.height = height + 12;
        if (_panelShadow != null) _panelShadow.height = height;
        _layoutDirty = true;
    }

    private void RefreshSettingsScroll()
    {
        if (_settingsGeometry == null || _settingsViewport == null || _settingsContent == null) return;
        var layout = _settingsGeometry;
        _settingsContent.transform.localPosition = new Vector3(0, _settingsNavigation.ScrollOffset, 0);
        _settingsViewport.baseClipRegion = new Vector4(0,
            Height / 2f - ExportPanelLayout.ContentTop - layout.ViewportHeight / 2f, 924, layout.ViewportHeight);
        bool scrollable = layout.MaximumScroll > 0;
        _settingsScrollRail?.SetActive(scrollable);
        if (!scrollable) return;
        PlaceSection(_settingsScrollRail, ExportPanelLayout.ContentTop);
        PlaceSection(_scrollDownButton, layout.ViewportHeight - 24);
        int trackHeight = layout.ViewportHeight - 64;
        int thumbHeight = Math.Max(28, (int)Math.Round(trackHeight * layout.ViewportHeight / (float)layout.ContentHeight));
        if (_scrollTrack != null) _scrollTrack.height = trackHeight;
        if (_scrollThumb != null)
        {
            _scrollThumb.height = thumbHeight;
            float offset = (trackHeight - thumbHeight) * _settingsNavigation.ScrollOffset / layout.MaximumScroll;
            _scrollThumb.transform.localPosition = new Vector3(959 - Width / 2f, Height / 2f - 32 - offset, 0);
        }
    }

    private void ScrollSettings(float delta)
    {
        if (_busy || _settingsGeometry == null || _settingsGeometry.MaximumScroll == 0) return;
        PollInputs();
        ReleaseInputFocus();
        CloseDropdown();
        _settingsNavigation.ScrollTo(_settingsNavigation.ScrollOffset + delta, _settingsGeometry);
        RefreshSettingsScroll();
        _refreshCadence.Invalidate();
    }

    private bool PointerOverSettingsContent()
    {
        if (_root == null || _nativeCamera == null || _settingsGeometry == null) return false;
        var pointer = UnityEngine.Input.mousePosition;
        pointer.z = _nativeCamera.WorldToScreenPoint(_root.transform.position).z;
        var point = _root.transform.InverseTransformPoint(_nativeCamera.ScreenToWorldPoint(pointer));
        float x = point.x + Width / 2f, y = Height / 2f - point.y;
        return x >= 28 && x <= 974 && y >= ExportPanelLayout.ContentTop
            && y <= ExportPanelLayout.ContentTop + _settingsGeometry.ViewportHeight;
    }

    private bool IsSettingsControlVisible(BoxCollider collider)
    {
        if (_settingsContent == null || _settingsGeometry == null || _root == null
            || !collider.transform.IsChildOf(_settingsContent.transform)) return true;
        var top = _root.transform.InverseTransformPoint(collider.transform.TransformPoint(
            collider.center + new Vector3(0, collider.size.y / 2f, 0)));
        var bottom = _root.transform.InverseTransformPoint(collider.transform.TransformPoint(
            collider.center - new Vector3(0, collider.size.y / 2f, 0)));
        return Height / 2f - top.y >= ExportPanelLayout.ContentTop - .5f
            && Height / 2f - bottom.y <= ExportPanelLayout.ContentTop + _settingsGeometry.ViewportHeight + .5f;
    }

    private void ToggleAdvancedSettings()
    {
        PollInputs();
        CloseDropdown();
        ReleaseInputFocus();
        _settingsNavigation.ToggleAdvanced();
        RefreshSettingsLayout();
        _refreshCadence.Invalidate();
    }

    private void BackToExportSettings()
    {
        PollInputs();
        CloseDropdown();
        ReleaseInputFocus();
        if (!_settingsNavigation.Back()) return;
        RefreshSettingsLayout();
        _refreshCadence.Invalidate();
    }

    private static void PlaceSection(GameObject? section, int y)
    {
        if (section != null) section.transform.localPosition = new Vector3(0, -y, 0);
    }

    private void BuildSharedFields(Transform parent)
    {
        Caption(parent, "作品名称", 44, 96);
        Input(parent, 44, 120, 892, 34, () => _options.Title, value =>
        {
            _options = _options with { Title = value };
            _titleEdited = true;
        }, 120);
        Caption(parent, "保存位置", 44, 164);
        Input(parent, 44, 188, 768, 34, () => OutputDirectoryPreference.ToPortableValue(_options.OutputDirectory),
            value => _options = _options with { OutputDirectory = value }, 32760);
        Button(parent, 824, 188, 112, 34, () => _picker.Busy ? "选择中…" : "浏览…",
            () => _picker.Open(_options.OutputDirectory), enabled: () => !_picker.Busy);
    }

    private void BuildVideo(Transform parent)
    {
        _outputSection = Child(parent, "Output canvas section");
        var output = _outputSection.transform;
        _outputCard = Rounded(output, "Output card", 32, 0, 916, 104, 14, Paper, 8);
        Label(output, "输出画面", 52, 12, 700, 26, 19);
        Caption(output, "分辨率", 52, 38);
        Dropdown(output, 52, 62, 340, () => _preset, ResolutionItems, SetResolution, height: 32);
        Caption(output, "画面比例", 416, 38);
        Dropdown(output, 416, 62, 238, () => _aspect, () => new[]
        {
            new DropdownItem("canvas", "当前窗口", $"使用 {SourceWidth} × {SourceHeight} 的比例"),
            new DropdownItem("16:9", "16:9 横屏", "按目标比例重新排版"),
            new DropdownItem("9:16", "9:16 竖屏", "按目标比例重新排版"),
            new DropdownItem("4:3", "4:3", "按目标比例重新排版"),
            new DropdownItem("1:1", "1:1 方形", "按目标比例重新排版")
        }, value =>
        {
            if (_aspect == value) return;
            _aspect = value;
            if (_preset == "native") _preset = "1080";
            if (_preset != "custom") SetResolution(_preset);
        }, height: 32);
        Caption(output, "帧率", 678, 38);
        Dropdown(output, 678, 62, 250, () => Number(_options.Fps), () => new[] { 24, 25, 30, 50, 60 }
            .Select(value => new DropdownItem(Number(value), value + " 帧/秒", value >= 50 ? "动作更流畅 · 渲染帧数更多" : "日常播放与视频制作")).ToArray(),
            value => _options = _options with { Fps = int.Parse(value, CultureInfo.InvariantCulture) }, height: 32);
        _customDimensionsSection = Child(output, "Custom dimensions");
        PlaceSection(_customDimensionsSection, 104);
        var dimensions = _customDimensionsSection.transform;
        Caption(dimensions, "自定义宽 × 高", 52, 0);
        Input(dimensions, 52, 26, 150, 34, () => _width, value => { _width = value; EditDimensions(); }, 5, UIInput.Validation.Integer);
        Label(dimensions, "×", 214, 29, 28, 28, 18, Muted, true);
        Input(dimensions, 252, 26, 150, 34, () => _height, value => { _height = value; EditDimensions(); }, 5, UIInput.Validation.Integer);
        Label(dimensions, "宽高使用偶数像素；保持你需要的画面比例。", 428, 31, 500, 26, 14, Muted);

        _superSection = Child(parent, "Super resolution section");
        var super = _superSection.transform;
        _superCard = Rounded(super, "Super resolution card", 32, 0, 916, 60, 14, Paper, 8);
        Label(super, "超分辨率", 52, 17, 150, 28, 20);
        Toggle(super, 694, 12, 234, "开启超分辨率", () => _options.SuperResolutionEnabled,
            value => _options = (_options with { SuperResolutionEnabled = value, CaptureMode = "native" })
                .NormalizeSuperResolutionForOutput(), () => _options.IsSuperResolutionTierAvailable("quality"));
        var disabled = Child(super, "Native rendering description");
        DynamicLabel(disabled.transform, () => _options.IsSuperResolutionTierAvailable("quality")
            ? $"原生 {_options.Width} × {_options.Height} · 开启后按所选档位放大"
            : "输出短边不足 720 像素，使用原生渲染", 214, 19, 458, 24, 14, Muted);
        _refresh.Add(new RefreshBinding(super.gameObject, () => disabled.SetActive(!_options.SuperResolutionEnabled)));
        _superControls = Child(super, "Enabled model controls");
        var controls = _superControls.transform;
        Caption(controls, "放大模型", 52, 50);
        Dropdown(controls, 52, 76, 436, () => _options.UpscaleAlgorithm, UpscaleModelItems,
            value => _options = _options with { UpscaleAlgorithm = value }, height: 36);
        Caption(controls, "画质档位", 518, 50);
        var tiers = new[] { ("quality", "质量"), ("balanced", "平衡"), ("performance", "性能") };
        for (int i = 0; i < tiers.Length; i++)
        {
            var tier = tiers[i];
            Button(controls, 518 + i * 138, 76, 130, 36, () => tier.Item2,
                () => _options = _options with { SuperResolutionTier = tier.Item1 },
                selected: () => _options.SuperResolutionTier == tier.Item1,
                enabled: () => _options.IsSuperResolutionTierAvailable(tier.Item1));
        }
        DynamicLabel(controls, SuperResolutionSummary, 52, 126, 452, 27, 14, Muted);
        DynamicLabel(controls, () => _options.IsSuperResolutionTierAvailable("performance")
            ? "平衡档兼顾内部清晰度与渲染速度。"
            : $"性能档不可用：当前内部短边须至少 {_options.MinimumSuperResolutionShortEdge} 像素。", 518, 120, 410, 36, 13, Muted);
        _sharpnessSection = Child(super, "Conditional RCAS sharpness");
        PlaceSection(_sharpnessSection, 164);
        var sharpness = _sharpnessSection.transform;
        Texture(sharpness, "Sharpness divider", 52, 0, 876, 1, Line, 10);
        Label(sharpness, "RCAS 锐度", 52, 23, 116, 24, 15, Muted);
        BuildSharpnessSlider(sharpness, 178, 31, 600);
        Input(sharpness, 806, 15, 122, 36, () => _rcasSharpness, value =>
        {
            _rcasSharpness = value;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)
                && double.IsFinite(amount) && amount is >= 0 and <= 1)
                _options = _options with { RcasSharpness = amount };
        }, 4, UIInput.Validation.Float);
    }

    private IReadOnlyList<DropdownItem> UpscaleModelItems() => new[]
    {
        new DropdownItem("anime4k-cnn", "Anime4K", "默认 · 动漫画面放大与降噪", _options.Codec != "qtrle"),
        new DropdownItem("anime4k-rcas", "Anime4K → FSR RCAS", "放大降噪后锐化 · 锐度可调", _options.Codec != "qtrle"),
        new DropdownItem("fsr1-luma", "FSR1（空间放大）", "EASU + RCAS · 亮度通道移植版", _options.Codec != "qtrle"),
        new DropdownItem("bilinear", "双线性", "轻量插值 · 偏向速度"),
        new DropdownItem("bicubic", "双三次", "平滑插值"),
        new DropdownItem("lanczos", "Lanczos", "锐利插值 · 可能出现边缘振铃"),
        new DropdownItem("xess", "XeSS（暂不可用）", "所需渲染数据尚未接入", false),
        new DropdownItem("dlss", "DLSS（暂不可用）", "所需渲染数据与 SDK 尚未接入", false)
    };

    private string TierTitle() => _options.SuperResolutionTier switch { "quality" => "质量", "performance" => "性能", _ => "平衡" };

    private void BuildSharpnessSlider(Transform parent, int x, int y, int width)
    {
        var owner = Child(parent, "Native RCAS slider");
        // NGUI's pointer conversion is relative to the slider component.
        // Keep the component and both track widgets at the same local origin.
        owner.transform.localPosition = new Vector3(x - Width / 2f, Height / 2f - y, 0);
        var track = Rounded(owner.transform, "Slider track", Width / 2, Height / 2, width, 6, 3, Line, 20);
        var collider = AddCollider(track.gameObject, width, 28);
        collider.center = new Vector3(width / 2f, -3, 0);
        _settingsSliderColliders.Add(collider);
        var fill = Texture(owner.transform, "Slider fill", Width / 2, Height / 2, width, 6, Cyan, 21);
        var thumb = Rounded(owner.transform, "Slider thumb", Width / 2, Height / 2, 20, 20, 10, Ink, 22);
        // UIProgressBar places the thumb transform at the track's centre line.
        // Its widget and hit target must therefore use a centred pivot too.
        thumb.pivot = UIWidget.Pivot.Center;
        var thumbCollider = AddCollider(thumb.gameObject, 24, 24);
        thumbCollider.center = Vector3.zero;
        _settingsSliderColliders.Add(thumbCollider);
        var slider = owner.AddComponent<UISlider>();
        slider.backgroundWidget = track;
        slider.foregroundWidget = fill;
        slider.thumb = thumb.transform;
        slider.fillDirection = UIProgressBar.FillDirection.LeftToRight;
        slider.numberOfSteps = 101;
        slider.value = (float)_options.RcasSharpness;
        _sharpnessSlider = slider;
        var edits = new SliderEditGuard(slider.value);
        bool synchronizing = false;
        Action changed = () =>
        {
            if (_busy || synchronizing || !_options.SuperResolutionEnabled || _options.UpscaleAlgorithm != "anime4k-rcas") return;
            if (!edits.ObserveChange(slider.value)) return;
            double value = Math.Round(slider.value, 2);
            _options = _options with { RcasSharpness = value };
            _rcasSharpness = value.ToString("0.00", CultureInfo.InvariantCulture);
            _refreshCadence.Invalidate();
        };
        var callback = DelegateSupport.ConvertDelegate<EventDelegate.Callback>(changed)
            ?? throw new InvalidOperationException("Cannot bind native sharpness slider.");
        EventDelegate.Add(slider.onChange, callback);
        _delegates.Add(changed); _delegates.Add(callback);
        _refresh.Add(new RefreshBinding(owner, () =>
        {
            float target = (float)_options.RcasSharpness;
            if (Math.Abs(slider.value - target) <= .005) return;
            synchronizing = true;
            try { slider.value = target; }
            finally { edits.Synchronize(slider.value); synchronizing = false; }
        }));
    }

    private void BuildAudio(Transform parent)
    {
        _audioSection = Child(parent, "Format and audio section");
        var page = _audioSection.transform;
        Rounded(page, "Format and audio card", 32, 0, 916, 172, 14, Paper, 8);
        Label(page, "格式与音频", 52, 12, 700, 26, 19);
        Caption(page, "文件格式", 52, 38);
        Dropdown(page, 52, 62, 172, () => _options.Container, () => new[]
        {
            new DropdownItem("mp4", "MP4", "通用播放 · 建议 AAC 音频"),
            new DropdownItem("mov", "MOV", "剪辑母版 · 支持 PCM，不支持 AV1"),
            new DropdownItem("mkv", "MKV", "支持多种格式，播放设备需兼容")
        }, SetContainer, height: 32);
        Caption(page, "视频编码", 242, 38);
        Dropdown(page, 242, 62, 182, () => _options.Codec, CodecItems, value =>
        {
            if (_options.Codec == value) return;
            _options = _options with { Codec = value, Encoder = "auto", Container = value == "av1" && _options.Container == "mov" ? "mkv" : _options.Container };
        }, enabled: () => HasHardware, placeholder: HardwarePlaceholder, height: 32);
        Caption(page, "音频质量", 442, 38);
        Dropdown(page, 442, 62, 268, () => _options.AudioQuality, () => new[]
        {
            new DropdownItem("none", "关闭音频", "仅导出画面"),
            new DropdownItem("aac128", "AAC 128 Kbps", "较小文件"),
            new DropdownItem("aac192", "AAC 192 Kbps", "通用播放 · 默认"),
            new DropdownItem("aac320", "AAC 320 Kbps", "高质量有损音频"),
            new DropdownItem("pcm16", "PCM 16 位", "无损音频 · 建议 MOV / MKV"),
            new DropdownItem("pcm24", "PCM 24 位", "无损编辑母版 · 建议 MOV / MKV")
        }, value => _options = _options with { AudioQuality = value }, height: 32);
        Caption(page, "采样率", 728, 38);
        Dropdown(page, 728, 62, 200, () => Number(_options.AudioSampleRate), () => new[]
        {
            new DropdownItem("44100", "44.1 kHz", "音乐常用采样率"),
            new DropdownItem("48000", "48 kHz", "视频常用采样率 · 默认")
        }, value => _options = _options with { AudioSampleRate = int.Parse(value, CultureInfo.InvariantCulture) },
            enabled: () => _options.AudioQuality != "none", placeholder: () => "音频已关闭", height: 32);
        DynamicLabel(page, () => _options.AudioQuality.StartsWith("pcm", StringComparison.Ordinal) && _options.Container == "mp4"
            ? "MP4 + PCM 的播放器兼容有限，编辑母版建议使用 MOV。"
            : "包含剧情的背景音乐、语音和音效。", 52, 100, 876, 22, 13, Muted);
        Toggle(page, 52, 130, 270, "在线播放优化", () => _options.FastStart,
            value => _options = _options with { FastStart = value }, () => _options.Container != "mkv");
        Toggle(page, 350, 130, 270, "保存封面 PNG", () => _options.WriteCover,
            value => _options = _options with { WriteCover = value });
        Toggle(page, 648, 130, 280, "显示菜单与自动按钮", () => ShowPlaybackButtons, value => ShowPlaybackButtons = value);
    }

    private void BuildAdvanced(Transform parent)
    {
        _advancedEntry = Child(parent, "Advanced settings entry");
        var entry = _advancedEntry.transform;
        Button(entry, 44, 0, 892, 36,
            () => (_settingsNavigation.IsAdvanced ? "▴  收起高级设置" : "▾  高级设置") + "    ·  " + EncoderSummary()
                + "  ·  " + (_options.RateControl == "cbr" ? "CBR" : "VBR") + "  ·  "
                + VideoBitrate.FormatMbps(_options.BitrateKbps) + " Mbps",
            ToggleAdvancedSettings, leftAligned: true);

        _advancedSection = Child(parent, "Advanced encoder accordion");
        var body = _advancedSection.transform;
        Rounded(body, "Hardware encoder card", 32, 0, 916, 110, 14, Paper, 8);
        Caption(body, "硬件编码器", 52, 8);
        Dropdown(body, 52, 34, 700, () => _options.Encoder, EncoderItems,
            value => _options = _options with { Encoder = value }, enabled: () => HasHardware,
            placeholder: HardwarePlaceholder, height: 34);
        Button(body, 768, 34, 160, 34, () => "重新检测", () => ProbeRequested?.Invoke());
        DynamicLabel(body, () => "显卡：" + (string.IsNullOrWhiteSpace(_gpuName) ? "等待检测" : _gpuName),
            52, 74, 876, 24, 14, Muted);
        Rounded(body, "Bitrate controls card", 32, 124, 916, 154, 14, Paper, 8);
        Caption(body, "码率控制", 52, 132);
        Dropdown(body, 52, 158, 412, () => _options.RateControl, () => new[]
        {
            new DropdownItem("vbr", "VBR · 可变码率", "按画面复杂度分配码率"),
            new DropdownItem("cbr", "CBR · 恒定码率", "保持稳定的码率目标")
        }, value => _options = _options with { RateControl = value }, height: 34);
        Caption(body, "推荐设置", 506, 132);
        Button(body, 506, 158, 422, 34, () => (_recommendBitrate ? "✓  推荐 " : "恢复推荐 · ")
            + VideoBitrate.FormatMbps(VideoBitrate.RecommendedKbps) + " Mbps", () =>
        {
            _recommendBitrate = true;
            RecommendBitrate();
        }, selected: () => _recommendBitrate);
        _customBitrateSection = Child(body, "Target video bitrate");
        var custom = _customBitrateSection.transform;
        Label(custom, "目标码率\n（Mbps）", 52, 204, 116, 48, 15, Muted);
        BuildBitrateSlider(custom, 178, 226, 600);
        Input(custom, 806, 210, 122, 36, () => _bitrate, value =>
        {
            _recommendBitrate = false;
            _bitrate = value;
            if (VideoBitrate.TryParseMbps(value, out var rate)) _options = _options with { BitrateKbps = rate };
        }, 7, UIInput.Validation.Float);
        Label(custom, "滑动调整 0.1–50 Mbps；更高码率可直接输入。", 178, 252, 750, 23, 13, Muted);
    }

    private void BuildBitrateSlider(Transform parent, int x, int y, int width)
    {
        var owner = Child(parent, "Native video bitrate slider");
        owner.transform.localPosition = new Vector3(x - Width / 2f, Height / 2f - y, 0);
        var track = Rounded(owner.transform, "Bitrate track", Width / 2, Height / 2, width, 6, 3, Line, 20);
        var collider = AddCollider(track.gameObject, width, 28);
        collider.center = new Vector3(width / 2f, -3, 0);
        _settingsSliderColliders.Add(collider);
        var fill = Texture(owner.transform, "Bitrate fill", Width / 2, Height / 2, width, 6, Cyan, 21);
        var thumb = Rounded(owner.transform, "Bitrate thumb", Width / 2, Height / 2, 20, 20, 10, Ink, 22);
        thumb.pivot = UIWidget.Pivot.Center;
        var thumbCollider = AddCollider(thumb.gameObject, 24, 24);
        thumbCollider.center = Vector3.zero;
        _settingsSliderColliders.Add(thumbCollider);
        var slider = owner.AddComponent<UISlider>();
        slider.backgroundWidget = track;
        slider.foregroundWidget = fill;
        slider.thumb = thumb.transform;
        slider.fillDirection = UIProgressBar.FillDirection.LeftToRight;
        slider.numberOfSteps = 500;
        slider.value = VideoBitrate.SliderPosition(_options.BitrateKbps);
        var edits = new SliderEditGuard(slider.value);
        bool synchronizing = false;
        Action changed = () =>
        {
            if (_busy || synchronizing) return;
            // Activation can happen before the throttled options-to-slider
            // refresh. Preserve valid, partial and invalid typed values then too.
            if (!edits.ObserveChange(slider.value)) return;
            int rate = VideoBitrate.FromSlider(slider.value);
            _recommendBitrate = false;
            _options = _options with { BitrateKbps = rate };
            _bitrate = VideoBitrate.FormatMbps(rate);
            _refreshCadence.Invalidate();
        };
        var callback = DelegateSupport.ConvertDelegate<EventDelegate.Callback>(changed)
            ?? throw new InvalidOperationException("Cannot bind native bitrate slider.");
        EventDelegate.Add(slider.onChange, callback);
        _delegates.Add(changed); _delegates.Add(callback);
        _refresh.Add(new RefreshBinding(owner, () =>
        {
            float target = VideoBitrate.SliderPosition(_options.BitrateKbps);
            if (Math.Abs(slider.value - target) < .5f / 499) return;
            // Manual values above the slider range keep their full encoding value.
            synchronizing = true;
            try { slider.value = target; }
            finally { edits.Synchronize(slider.value); synchronizing = false; }
        }));
    }

    private string EncoderSummary()
    {
        if (!HasHardware) return HardwarePlaceholder();
        string selected;
        try { selected = HardwareEncoderPolicy.Select(_options, _encoders!, _gpuName); }
        catch (ExportException) { return "硬件自动选择"; }
        string vendor = selected.EndsWith("_nvenc", StringComparison.Ordinal) ? "NVIDIA"
            : selected.EndsWith("_amf", StringComparison.Ordinal) ? "AMD" : "Intel";
        return (_options.Encoder == "auto" ? "自动 · " : "") + vendor;
    }

    private string ExportSummary()
    {
        string size = _preset is "1080" or "1440" or "2160" or "720" ? _preset + "P" : $"{_options.Width} × {_options.Height}";
        string render = "原生渲染";
        if (_options.SuperResolutionEnabled)
        {
            var canvas = _options.GetCanvasLayout();
            render = UpscaleModelTitle() + " / " + TierTitle() + " · 内部 " + Math.Min(canvas.CaptureWidth, canvas.CaptureHeight) + "P";
        }
        return $"{size} · {_options.Fps} 帧 · {_options.Container.ToUpperInvariant()} · {render}";
    }

    private string ReadyMessage()
    {
        if (!string.IsNullOrWhiteSpace(_notice)) return _notice;
        if (_status.StartsWith("GPU 检测完成", StringComparison.Ordinal) || _status.StartsWith("选择剧情", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(_status))
            return HasHardware ? "硬件加速已就绪" : HardwarePlaceholder();
        return _status;
    }

    private void BuildStatus(Transform parent)
    {
        _footerSection = Child(parent, "Export summary and actions");
        var footer = _footerSection.transform;
        Texture(footer, "Footer divider", 44, 0, 892, 1, Line, 10);
        // Keep the existing diagnostic counters; progress itself has its own page.
        _progressWidget = Texture(footer, "Settings progress", 44, 0, 1, 1, Cyan, 11);
        DynamicLabel(footer, ExportSummary, 44, 10, 892, 24, 16);
        DynamicLabel(footer, ReadyMessage, 44, 38, 892, 34, 14, Muted);
        DynamicLabel(footer, () => _settingsGeometry?.MaximumScroll > 0 ? "滚轮或右侧箭头查看更多设置" : "Ctrl + Shift + E  显示 / 隐藏", 44, 85, 440, 24, 13, Muted);
        Button(footer, 548, 78, 126, 34, () => "关闭", () => Visible = false);
        Button(footer, 694, 78, 242, 34,
            () => _busy ? "正在导出…" : _encoders == null ? "检测硬件中…" : !HasHardware ? "无可用硬件编码器" : "开始导出",
            RequestExport, primary: true, enabled: () => !_picker.Busy && HasHardware);
    }
}
