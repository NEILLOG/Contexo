using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Contexo.App.Services;
using Contexo.Desktop.Views.Shell;

namespace Contexo.Desktop.Tests;

public sealed class DialogTests
{
    private static async Task<bool> WithTimeout(Task<bool> task)
    {
        for (var i = 0; i < 200 && !task.IsCompleted; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.True(task.IsCompleted, "The dialog did not finish");
        Dispatcher.UIThread.RunJobs();
        return await task;
    }

    private static (MainWindow Window, TestShell Test) Open()
    {
        var test = new TestShell();
        var window = test.CreateWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, test);
    }

    private static T Find<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public async Task Esc_cancels_the_dialog()
    {
        var (window, test) = Open();

        var result = test.Dialogs.ConfirmAsync(new ConfirmRequest("移除資料夾", ["原始檔案不會被刪除"], "移除"));
        Dispatcher.UIThread.RunJobs();
        Assert.True(test.Dialogs.IsOpen);
        Assert.IsType<ConfirmDialogView>(Find<ContentControl>(window, "DialogContent").Presenter?.Child);
        ScreenshotHelper.Capture(window, "dialog-confirm");

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        Assert.False(await WithTimeout(result));
        Assert.False(test.Dialogs.IsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Enter_confirms_an_ordinary_dialog()
    {
        var (window, test) = Open();

        var result = test.Dialogs.ConfirmAsync(new ConfirmRequest("加入資料夾", [], "加入"));
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        Assert.True(await WithTimeout(result));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Enter_does_not_confirm_a_dangerous_action()
    {
        var (window, test) = Open();

        var result = test.Dialogs.ConfirmAsync(new ConfirmRequest("清除全部資料", ["需要重新建立"], "清除", IsDestructive: true));
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(result.IsCompleted);
        Assert.True(test.Dialogs.IsOpen);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.False(await WithTimeout(result));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Confirm_needs_the_acknowledgement_box_when_one_is_requested()
    {
        var (window, test) = Open();

        var result = test.Dialogs.ConfirmAsync(new ConfirmRequest(
            "清除全部資料", ["需要重新建立"], "清除", IsDestructive: true, AcknowledgeText: "我了解需要重新建立，可能要數小時"));
        Dispatcher.UIThread.RunJobs();

        var confirm = Find<Button>(window, "ConfirmButton");
        var box = Find<CheckBox>(window, "AcknowledgeBox");
        Assert.False(confirm.IsEffectivelyEnabled);
        Assert.Equal("我了解需要重新建立，可能要數小時", box.Content);
        ScreenshotHelper.Capture(window, "dialog-acknowledge-unchecked");

        box.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(confirm.IsEffectivelyEnabled);
        ScreenshotHelper.Capture(window, "dialog-acknowledge-checked");

        confirm.Command!.Execute(null);
        Assert.True(await WithTimeout(result));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Enter_waits_for_the_acknowledgement_box()
    {
        var (window, test) = Open();

        var result = test.Dialogs.ConfirmAsync(new ConfirmRequest("確認", [], "確定", AcknowledgeText: "我了解"));
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(result.IsCompleted);

        Find<CheckBox>(window, "AcknowledgeBox").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        Assert.True(await WithTimeout(result));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Information_dialog_has_no_cancel_button()
    {
        var (window, test) = Open();

        var result = test.Dialogs.ConfirmAsync(new ConfirmRequest("Contexo 會在背景繼續執行", ["可以從圖示再打開"], "知道了", CancelText: null));
        Dispatcher.UIThread.RunJobs();

        Assert.False(Find<Button>(window, "CancelButton").IsEffectivelyVisible);
        Find<Button>(window, "ConfirmButton").Command!.Execute(null);
        Assert.True(await WithTimeout(result));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Second_dialog_waits_for_the_first()
    {
        var (window, test) = Open();

        var first = test.Dialogs.ConfirmAsync(new ConfirmRequest("第一個", [], "好"));
        var second = test.Dialogs.ConfirmAsync(new ConfirmRequest("第二個", [], "好"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("第一個", test.Dialogs.Title);

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Assert.True(await WithTimeout(first));

        Dispatcher.UIThread.RunJobs();
        for (var i = 0; i < 100 && test.Dialogs.Title != "第二個"; i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal("第二個", test.Dialogs.Title);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.False(await WithTimeout(second));
        window.Close();
    }
}
