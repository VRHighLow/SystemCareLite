using System;
using System.Drawing;
using System.Windows.Forms;

namespace SystemCareLite
{
    public class TrayAppContext : ApplicationContext
    {
        private NotifyIcon trayIcon;
        private MainPopup popupForm;

        public TrayAppContext()
        {
            trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                ContextMenuStrip = new ContextMenuStrip(),
                Visible = true,
                Text = "SystemCare Lite"
            };

            trayIcon.ContextMenuStrip.Items.Add("Open", null, ShowPopup);
            trayIcon.ContextMenuStrip.Items.Add("Exit", null, Exit);
            trayIcon.MouseClick += TrayIcon_MouseClick;

            popupForm = new MainPopup();
        }

        private void TrayIcon_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                ShowPopup(sender, e);
        }

        private void ShowPopup(object sender, EventArgs e)
{

    if (popupForm == null || popupForm.IsDisposed)
    {
        popupForm = new MainPopup();
    }

    if (popupForm.Visible)
    {
        popupForm.Hide();
    }
    else
    {
        // Position at bottom‑right of the screen
        popupForm.StartPosition = FormStartPosition.Manual;
        var wa = Screen.PrimaryScreen.WorkingArea;
        popupForm.Location = new Point(
            wa.Right - popupForm.Width - 10,
            wa.Bottom - popupForm.Height - 10
        );
        popupForm.Show();
        popupForm.BringToFront();
    }
}


        private void Exit(object sender, EventArgs e)
        {
            trayIcon.Visible = false;
            Application.Exit();
        }
    }
}
