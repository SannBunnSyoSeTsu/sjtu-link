using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
internal static class UiSmoke {
    static readonly BindingFlags Fields=BindingFlags.NonPublic|BindingFlags.Instance;
    static T Field<T>(Client c,string name) { return (T)typeof(Client).GetField(name,Fields).GetValue(c); }
    static void CheckLayout(float scale) {
        using(var client=new Client(true)) {
            client.Show(); client.Scale(new SizeF(scale,scale));
            var status=Field<Label>(client,"status"); status.Text="已连接";
            client.PerformLayout(); Application.DoEvents();
            int height=TextRenderer.MeasureText(status.Text,status.Font).Height;
            if(status.Height<height)throw new Exception("Status glyphs clipped at scale "+scale);
            var detail=Field<Label>(client,"statusDetail");
            if(status.Bounds.IntersectsWith(detail.Bounds))throw new Exception("Status overlaps detail at scale "+scale);
            var labels=status.Parent.Controls.OfType<Label>().ToArray();
            var left=labels.Single(l=>l.Text.StartsWith("VPN 目标"));
            var right=labels.Single(l=>l.Text.StartsWith("预览 ·"));
            if(left.Bounds.IntersectsWith(right.Bounds))throw new Exception("Column captions overlap at scale "+scale);
            using(var bitmap=new Bitmap(client.Width,client.Height)) {
                client.DrawToBitmap(bitmap,new Rectangle(Point.Empty,client.Size));
                bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"layout-"+(int)(scale*100)+".png"));
            }
            Console.WriteLine("PASS: status glyph bounds and caption separation at layout scale "+scale);
            client.Close();
        }
    }
    [STAThread] static void Main() {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        foreach(float scale in new [] {1F,1.25F,1.5F})CheckLayout(scale);
        using(var client=new Client(true)) {
            client.Shown += async delegate {
                try {
                    if(Field<ComboBox>(client,"server").Text!="stu.vpn.sjtu.edu.cn")throw new Exception("Server selection missing");
                    var rules=Field<TextBox>(client,"rules"); rules.Text="202.120.2.100\r\n2001:db8::123/48";
                    await (Task)typeof(Client).GetMethod("Preview",Fields).Invoke(client,null);
                    if(Field<ListView>(client,"routeList").Items.Count!=2)throw new Exception("Preview rows not rendered");
                    Field<ComboBox>(client,"mode").SelectedIndex=1;
                    if(Field<ListView>(client,"routeList").Items.Count!=0 || !Field<bool>(client,"draftNeedsApply"))throw new Exception("Mode change did not invalidate plan");
                    Console.WriteLine("PASS: desktop server selection, preview result rendering, pending-change invalidation.");
                } catch(Exception e) { Console.WriteLine(e); Environment.ExitCode=1; }
                finally { typeof(Client).GetField("dirty",Fields).SetValue(client,false); client.Close(); }
            };
            Application.Run(client);
        }
    }
}
