using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Tokenmetry.App.Services;

/// <summary>
/// 文件管理器出口（三平台）：打开一个目录、或在目录中选中一个文件。两处调用方（设置页「本地路径」
/// 的打开数据目录、更新包下载后的 reveal）共用同一套平台分支，避免两份 explorer/open/xdg-open 方言。
///
/// Best-effort 语义——失败只打诊断，绝不抛：调用方都是「顺手帮你跳一下」的辅助动作，为一次
/// 「打不开文件管理器」（Linux 可能没装 xdg-open）弹错误框打断主流程不划算。
/// </summary>
public static class ShellLauncher
{
    /// <summary>在系统文件管理器中打开目录（不存在/无文件管理器时只打诊断）。</summary>
    public static void OpenFolder(string folderPath) => Launch(folderPath, select: false);

    /// <summary>在系统文件管理器中显示某个文件（落在其所在目录并选中；Linux 无跨桌面选中标准，回落为打开目录）。</summary>
    public static void RevealFile(string filePath) => Launch(filePath, select: true);

    private static void Launch(string path, bool select)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // explorer 对参数自己做解析（不用标准引号规则）：选中文件时 /select,"path" 必须拼成单串；
                // 打开目录直接把路径给它（含空格的路径也能接住）。
                var arguments = select ? $"/select,\"{path}\"" : $"\"{path}\"";
                Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start(new ProcessStartInfo("open", select ? $"-R \"{path}\"" : $"\"{path}\"")
                {
                    UseShellExecute = true,
                });
            }
            else
            {
                // xdg-open 只接受目录：选中场景回落到打开文件所在目录。
                var target = select ? Path.GetDirectoryName(path)! : path;
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{target}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] 打开文件管理器失败（{path}）：{ex.Message}");
        }
    }
}
