using System.ComponentModel;

namespace PinyinTranslate.Input;

public enum CandidateObserverFailureCode
{
    Unexpected,
    AccessDenied,
    TargetExited,
    BridgePackageMissing,
    StartupTimeout,
    ObserverInitializationFailed,
    BridgeExited,
    TransportUnavailable,
    ProtocolError
}

/// <summary>Metadata only. Never forward provider exception messages or document contents.</summary>
public sealed record CandidateObserverFailure(CandidateObserverFailureCode Code, string Stage,
    string ErrorType = "", int HResult = 0, int? NativeErrorCode = null, int? ChildExitCode = null)
{
    public static CandidateObserverFailure FromException(Exception error, string stage)
    {
        int? native = (error as Win32Exception)?.NativeErrorCode;
        var code = error switch
        {
            _ when error.HResult == unchecked((int)0x80070005) => CandidateObserverFailureCode.AccessDenied,
            UnauthorizedAccessException => CandidateObserverFailureCode.AccessDenied,
            Win32Exception when native == 5 => CandidateObserverFailureCode.AccessDenied,
            ArgumentException when stage is "target-query" or "binding" => CandidateObserverFailureCode.TargetExited,
            FileNotFoundException or DirectoryNotFoundException => CandidateObserverFailureCode.BridgePackageMissing,
            TimeoutException or OperationCanceledException when stage == "running" => CandidateObserverFailureCode.TransportUnavailable,
            TimeoutException or OperationCanceledException => CandidateObserverFailureCode.StartupTimeout,
            InvalidDataException => CandidateObserverFailureCode.ProtocolError,
            IOException => CandidateObserverFailureCode.TransportUnavailable,
            _ when stage.StartsWith("uia-", StringComparison.Ordinal) => CandidateObserverFailureCode.ObserverInitializationFailed,
            _ => CandidateObserverFailureCode.Unexpected
        };
        return new(code, stage, error.GetType().Name, error.HResult, native);
    }

    public string ToUserMessage() => Code switch
    {
        CandidateObserverFailureCode.AccessDenied => "Windows 拒绝访问目标程序或候选服务；请确认目标程序与翻译工具的运行权限是否匹配。",
        CandidateObserverFailureCode.TargetExited => "目标程序已退出或输入窗口已失效。",
        CandidateObserverFailureCode.BridgePackageMissing => "候选服务文件不完整；请重新解压整个发行包，保留全部配套文件。",
        CandidateObserverFailureCode.StartupTimeout => "候选服务启动或连接超时。",
        CandidateObserverFailureCode.ObserverInitializationFailed => "Windows 输入法读取接口初始化或运行失败。",
        CandidateObserverFailureCode.BridgeExited => "候选读取服务已退出。",
        CandidateObserverFailureCode.TransportUnavailable => "候选服务的本机连接已断开。",
        CandidateObserverFailureCode.ProtocolError => "候选服务通信或身份校验失败；请使用同一发行包中的全部文件。",
        _ => "候选服务暂时不可用。"
    };
}
