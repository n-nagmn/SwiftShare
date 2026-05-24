using System;
using System.Drawing;
using System.Windows.Forms;

public class TestForm : Form {
    public TestForm() {
        this.Size = new Size(800, 600);
        Button b1 = new Button() { Location = new Point(100, 100), Size = new Size(200, 50), Text = "Test" };
        this.Controls.Add(b1);
        
        // Output original
        Console.WriteLine(string.Format("Original: Button Size = {0}, Location = {1}, Font = {2}", b1.Size, b1.Location, b1.Font.Size));
        
        Panel p = new Panel() { Location = new Point(50, 50), Size = new Size(100, 100) };
        Button b2 = new Button() { Location = new Point(10, 10), Size = new Size(50, 50) };
        p.Controls.Add(b2);
        
        p.Scale(new SizeF(2.0f, 2.0f));
        
        Console.WriteLine(string.Format("Panel Size = {0}, Button Size = {1}, Button Location = {2}", p.Size, b2.Size, b2.Location));
    }
    
    [STAThread]
    public static void Main() {
        new TestForm();
    }
}
