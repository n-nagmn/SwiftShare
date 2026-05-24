using System;
using System.Drawing;
using System.Windows.Forms;
using FileTransferApp;

public class TestRender {
    [STAThread]
    public static void Main() {
        var form = new SwiftShare();
        form.Show();
        Bitmap bmp = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
        bmp.Save(@"C:\Users\ひまわり\SwiftShareRepo\layout_test.png");
        form.Close();
    }
}
