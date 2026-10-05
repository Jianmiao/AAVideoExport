namespace AAVideoExport.Core;

// Translate at the presentation boundary. Error codes and original diagnostic
// details remain available to callers and the Mod log.
public static class ExportErrorText
{
    public static string Describe(Exception error)
    {
        while (error is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
            error = aggregate.InnerExceptions[0];
        if (error is ExportException export)
        {
            if (export.Code == "encode_failed" && export.InnerException is ExportException cause)
                return Describe(cause);
            return export.Code switch
            {
                "output_exists" => "保存位置已有同名的视频或封面。请修改文件名称或选择其他保存位置，现有文件不会被覆盖。",
                "disk_full" => "保存磁盘空间不足，请清理空间或更换保存位置。",
                "io_error" => DescribeIo(export.InnerException ?? export),
                "cancelled" => "导出已取消。",
                "hardware_encoder_required" => "请使用通过本机检测的硬件编码器，再开始导出。",
                "hardware_encoder_unavailable" => "当前视频格式没有可用的硬件编码器。请重新检测、更新显卡驱动，或切换为 CPU 软件编码。",
                "software_encoder_required" => "请使用通过本机检测的软件编码器，再开始导出。",
                "software_encoder_unavailable" => "当前视频格式没有可用的软件编码器。请重新检测或更换视频编码；H.264 软件编码需要 FFmpeg 包含 libx264。",
                "encoder_unavailable" => "当前视频格式没有可用的编码器。请重新检测或更换视频编码。",
                "encoder_failed" or "encode_failed" => "视频编码或音视频合成失败。请检查视频与音频格式、显卡驱动和保存位置，详细原因已记录在 Mod 日志中。",
                "process_start_failed" => "无法启动视频处理组件。请检查 FFmpeg 安装位置和文件是否完整。",
                "process_timeout" => "视频处理长时间没有响应，导出已停止。请检查显卡驱动及磁盘状态后重试。",
                "upscaler_unavailable" => StartsWithChinese(export.Message) ? export.Message
                    : "所选放大模型未通过本机检测。请检查显卡驱动，或关闭超分辨率、改用插值模型后重试。",
                "invalid_settings" => Validation(export.Message),
                "invalid_frame" => "视频帧的数据或尺寸异常，导出已停止。请重新加载剧情后重试。",
                "gpu_readback_failed" => "显卡读取视频帧失败，导出已停止。请检查显卡驱动后重试。",
                "invalid_audio" => "音频数据异常，导出已停止。请重新加载剧情或关闭音频后重试。",
                "audio_timeline_mismatch" => "录制的音频与画面时长不一致，导出已停止。请重新加载剧情后重试。",
                "verification_failed" => "导出文件未通过完整性检查。请查看 Mod 日志中的详细原因后重试。",
                "invalid_state" => "当前导出任务状态异常，请返回设置重新开始导出。",
                _ => StartsWithChinese(export.Message) ? export.Message : "导出发生异常，请查看 Mod 日志中的详细原因后重试。"
            };
        }
        return error switch
        {
            OperationCanceledException => "导出已取消。",
            UnauthorizedAccessException or IOException => DescribeIo(error),
            TimeoutException => "操作超时，请稍后重试。",
            _ => "导出发生异常，请查看 Mod 日志中的详细原因后重试。"
        };
    }

    private static string DescribeIo(Exception error) => error switch
    {
        UnauthorizedAccessException => "保存位置没有写入权限。请选择可写入的文件夹。",
        FileNotFoundException => "找不到所需的文件或视频处理组件，请检查安装文件是否完整。",
        DirectoryNotFoundException => "保存位置或所需目录不存在，请重新选择文件夹。",
        IOException when (error.HResult & 0xffff) is 32 or 33 => "文件正在被其他程序占用，请关闭占用程序或更换文件名称后重试。",
        _ => "无法读写导出文件，请检查保存位置、磁盘空间以及文件是否被其他程序占用。"
    };

    private static bool StartsWithChinese(string text) => text.Length > 0 && text[0] is >= '\u4e00' and <= '\u9fff';

    private static string Validation(string message) => message switch
    {
        "Title must contain 1–120 characters." => "文件名称须为 1–120 个字符。",
        "Title cannot contain traversal segments or end with a dot or space." or
        "Title contains a character that cannot be used in a filename." or "Title is a reserved device name."
            => "文件名称含有无效或系统保留字符，请修改名称后重试。",
        "Choose a valid absolute output folder without control characters." => "保存路径无效，请重新选择文件夹或填写完整路径。",
        "Frame rate must be 24, 25, 30, 50, or 60." => "请选择 24、25、30、50 或 60 帧/秒。",
        "Container must be mp4, mov, or mkv." => "请选择 MP4、MOV 或 MKV 文件格式。",
        "Unsupported video codec." => "当前视频编码不受支持，请重新选择视频编码。",
        "Encoding mode must be hardware or software." => "请选择 GPU 硬件编码或 CPU 软件编码。",
        "Lossless QuickTime Animation requires MOV." => "无损动画编码需要使用 MOV 文件格式。",
        "AV1 is offered in MP4 or MKV; use one of those containers." => "AV1 编码需要使用 MP4 或 MKV 文件格式。",
        "Rate control must be vbr or cbr." => "请选择 VBR 可变码率或 CBR 恒定码率。",
        "Bitrate must be 100–500000 kbps." => "目标码率须为 0.1–500 Mbps。",
        "Unsupported audio quality." => "当前音频质量不受支持，请重新选择音频设置。",
        "Audio sample rate must be 44100 or 48000 Hz." => "音频采样率须为 44.1 或 48 kHz。",
        "The selected encoder does not support this codec." or "The encoder does not match the selected codec."
            => "所选编码器不支持当前视频编码，请更换编码器或视频编码。",
        "Dimensions must be even, at least 16 pixels, and within 7680 × 4320 (or portrait equivalent)."
            => "画面宽高须为偶数且至少 16 像素，最长边不超过 7680、最短边不超过 4320。",
        "This fill composition needs a render target larger than the supported canvas. Choose fit or a lower output resolution."
            => "当前画布布局需要的渲染尺寸过大，请调整画布比例或降低输出分辨率。",
        "This tier would render below 720 pixels on the short edge. Choose a higher tier or native rendering."
            => "当前档位的内部渲染短边不足 720 像素，请选择更高画质档位或原生渲染。",
        "This super-resolution tier is unavailable for the selected output size. Choose a higher tier or native rendering."
            => "当前输出尺寸不支持所选超分档位，请选择质量或平衡档；低于 720P 的输出使用原生渲染。",
        "RCAS sharpness must be between 0 and 1." => "RCAS 锐度须为 0–1 之间的数字。",
        "Unsupported super-resolution tier." => "超分辨率画质档位不受支持，请重新选择档位。",
        "Unsupported upscaling algorithm." => "当前放大模型不受支持，请重新选择模型。",
        "GPU upscaling currently supports H.264, HEVC and AV1 outputs."
            => "当前超分模型需要使用 H.264、HEVC 或 AV1 视频编码。",
        _ => StartsWithChinese(message) ? message : "当前导出设置不受支持，请检查画面尺寸、视频编码和音频设置后重试。"
    };
}
