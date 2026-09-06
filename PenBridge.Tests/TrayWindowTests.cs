using System.Reflection;
using PenBridge.UI;

namespace PenBridge.Tests;

public class TrayWindowTests
{
    [Fact]
    public void Closing_hides_window_and_tray_menu_stays_short()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow();
                // No server is started and no settings are written by this lifecycle test.
                window.Show();
                var type = typeof(MainWindow);
                void Invoke(string method) => type.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);

                window.Close();
                Assert.False(window.IsVisible);
                Assert.False(window.ShowInTaskbar);

                Invoke("RestoreWindow");
                Assert.True(window.IsVisible);
                Assert.True(window.ShowInTaskbar);

                Invoke("BuildTrayMenu");
                var menu = (System.Windows.Forms.ContextMenuStrip)type.GetField("_trayMenu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                var labels = menu.Items.Cast<System.Windows.Forms.ToolStripItem>().Select(i => i.Text).ToArray();
                Assert.Contains("창 열기", labels);
                Assert.Contains("연결 상세 정보…", labels);
                Assert.Contains("PenBridge 종료", labels);
                // The tray menu is meant to stay a short list of actions, not a dump of live
                // connection stats (those belong in the "연결 상세 정보" window instead).
                Assert.True(menu.Items.Count <= 7, $"Expected a short tray menu, got {menu.Items.Count} items: {string.Join(", ", labels)}");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Tray UI test did not finish.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
