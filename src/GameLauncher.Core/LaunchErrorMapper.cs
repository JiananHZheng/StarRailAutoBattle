using System.ComponentModel;

namespace GameLauncher.Core;

public enum LaunchFailureCategory
{
    UserCancelledElevation,
    NotFound,
    AccessDenied,
    BlockedBySecurity,
    ElevationRequired,
    BadExecutable,
    NoAssociation,
    Other,
}

public sealed record LaunchFailure(LaunchFailureCategory Category, int Win32Error, string SystemMessage, string Message);

public static class LaunchErrorMapper
{
    public const int ErrorFileNotFound = 2;
    public const int ErrorPathNotFound = 3;
    public const int ErrorAccessDenied = 5;
    public const int ErrorBadExeFormat = 193;
    public const int ErrorExeMachineTypeMismatch = 216;
    public const int ErrorVirusInfected = 225;
    public const int ErrorVirusDeleted = 226;
    public const int ErrorElevationRequired = 740;
    public const int ErrorNoAssociation = 1155;
    public const int ErrorCancelled = 1223;
    public const int ErrorAccessDisabledByPolicy = 1260;
    public const int ErrorSystemIntegrityPolicyViolation = 4551;

    /// <summary>
    /// Maps a Win32 error from ShellExecuteEx to a category and a Chinese message with the next step.
    /// It deliberately does not guess the cause of "access denied".
    /// </summary>
    public static LaunchFailure Map(int win32Error, ExecutionLevel? executionLevel = null)
    {
        var failure = MapCode(win32Error, SystemMessage(win32Error));
        if (executionLevel == ExecutionLevel.RequireAdministrator && failure.Category == LaunchFailureCategory.AccessDenied)
        {
            failure = failure with { Message = failure.Message + " 注意：该程序的清单要求管理员权限，正常情况下 Windows 会弹出 UAC 确认。" };
        }

        return failure;
    }

    private static LaunchFailure MapCode(int win32Error, string system)
    {
        return win32Error switch
        {
            ErrorCancelled => new(LaunchFailureCategory.UserCancelledElevation, win32Error, system,
                "启动已取消：Windows 用户账户控制（UAC）提示中选择了“否”，或提示被关闭。如需启动，请重试并在提示中选择“是”；也可以改用官方启动器启动游戏。"),

            ErrorFileNotFound or ErrorPathNotFound => new(LaunchFailureCategory.NotFound, win32Error, system,
                "Windows 报告找不到要启动的文件或文件夹。程序或快捷方式目标可能已被移动或删除，请重新选择。"),

            ErrorAccessDenied => new(LaunchFailureCategory.AccessDenied, win32Error, system,
                "Windows 拒绝了这次启动（错误码 5：拒绝访问）。仅凭这个错误码无法判断原因。请依次检查：在资源管理器中双击同一个文件能否打开；" +
                "Windows 安全中心 →“保护历史记录”是否有拦截记录；该游戏是否要求通过官方启动器启动。本应用不会自动提权或关闭系统防护。"),

            ErrorElevationRequired => new(LaunchFailureCategory.ElevationRequired, win32Error, system,
                "这个程序要求管理员权限，Windows 未能弹出权限确认。请在资源管理器中双击它确认能否打开，或改用官方启动器。"),

            ErrorBadExeFormat or ErrorExeMachineTypeMismatch => new(LaunchFailureCategory.BadExecutable, win32Error, system,
                "Windows 无法把这个文件当作程序运行（文件可能损坏，或不适用于本机的处理器架构）。请重新选择正确的游戏或启动器程序。"),

            ErrorVirusInfected or ErrorVirusDeleted or ErrorAccessDisabledByPolicy or ErrorSystemIntegrityPolicyViolation =>
                new(LaunchFailureCategory.BlockedBySecurity, win32Error, system,
                    "Windows 安全功能或系统策略阻止了这个程序。请打开 Windows 安全中心 →“保护历史记录”查看详情，并确认文件来自官方渠道。请不要为此关闭系统防护。"),

            ErrorNoAssociation => new(LaunchFailureCategory.NoAssociation, win32Error, system,
                "Windows 不知道如何打开这个文件。请选择 .exe 程序或指向 .exe 的快捷方式。"),

            _ => new(LaunchFailureCategory.Other, win32Error, system,
                $"Windows 未能启动程序（错误码 {win32Error}：{system}）。请在资源管理器中双击同一个文件确认能否打开，或改用官方启动器。"),
        };
    }

    public static string SystemMessage(int win32Error)
    {
        try
        {
            return new Win32Exception(win32Error).Message;
        }
        catch (Exception)
        {
            return $"Win32 错误 {win32Error}";
        }
    }
}
