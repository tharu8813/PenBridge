using System.Reflection;
using PenBridge.UI;

namespace PenBridge.Tests;

public class TrayWindowTests
{
    [Fact]
    public void Closing_hides_window_and_tray_can_restore_it_with_details_menu()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new MainForm();
                // No server is started and no settings are written by this lifecycle test.
                form.Show();
                using (var bitmap = new System.Drawing.Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                    bitmap.Save(Path.Combine(AppContext.BaseDirectory, "tray-window-preview.png"));
                }
                var type = typeof(MainForm);
                void Invoke(string method) => type.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(form, null);
                form.Close();
                Assert.False(form.IsDisposed);
                Assert.False(form.Visible);
                Assert.False(form.ShowInTaskbar);
                Invoke("RestoreWindow");
                Assert.True(form.Visible);
                Assert.True(form.ShowInTaskbar);
                Invoke("BuildTrayMenu");
                var menu = (System.Windows.Forms.ContextMenuStrip)type.GetField("_trayMenu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
                var labels = menu.Items.Cast<System.Windows.Forms.ToolStripItem>().Select(i => i.Text).ToArray();
                Assert.Contains("연결된 클라이언트 없음", labels);
                Assert.Contains("연결 상세 정보…", labels);
                Assert.Contains("PenBridge 종료", labels);
                Assert.DoesNotContain(type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance), f => f.Name is "_monitorCombo" or "_mappingCombo" or "_allowNonPenCheck");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Tray UI test did not finish.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
