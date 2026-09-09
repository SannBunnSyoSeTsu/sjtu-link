using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyVersion("1.0.2.0")]
[assembly: AssemblyFileVersion("1.0.2.0")]
[assembly: AssemblyInformationalVersion("1.0.2")]
[assembly: AssemblyProduct("SJTU Link")]

internal static class Program {
    internal const string Profile = "SJTU Link - Student IKEv2";
    internal static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    internal static IEnumerable<object> Items(object value) { return ((IEnumerable)value).Cast<object>(); }
    internal static readonly string StateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SJTU-Link");
    [STAThread] static void Main(string[] args) {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Contains("--render")) {
            using (var f = new Client(true)) {
                f.Show(); Application.DoEvents();
                using (var bitmap = new Bitmap(f.Width, f.Height)) {
                    f.DrawToBitmap(bitmap, new Rectangle(Point.Empty, f.Size));
                    bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preview.png"));
                }
            }
            return;
        }
        Application.Run(new Client(false));
    }
    internal static async Task<object> Call(object request) {
        string script;
        using (var reader = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("backend.ps1"))) script = reader.ReadToEnd();
        const string bootstrap = "$wire=[Console]::In.ReadToEnd()|ConvertFrom-Json; $request=$wire.Request; & ([scriptblock]::Create([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($wire.Script))))";
        var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap))) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        using (var process = Process.Start(psi)) {
            // Encode the payload to ASCII before writing: no locale-dependent credentials or shell interpolation.
            var payload = Json.Serialize(new { Script=Convert.ToBase64String(Encoding.UTF8.GetBytes(script)), Request=request });
            var escaped = new StringBuilder();
            foreach (char c in payload) { if (c > 127) escaped.Append("\\u" + ((int)c).ToString("x4")); else escaped.Append(c); }
            await process.StandardInput.WriteAsync(escaped.ToString()); process.StandardInput.Close();
            Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
            await Task.Run(() => process.WaitForExit());
            string text = await output, error = await errors;
            Dictionary<string,object> response;
            try { response = Json.Deserialize<Dictionary<string,object>>(text.Trim()); }
            catch { throw new Exception("Windows VPN 服务返回了无法读取的结果。" + error); }
            if (!(bool)response["Ok"]) throw new Exception(Convert.ToString(response["Error"]));
            return response["Data"];
        }
    }
}

internal sealed class Client : Form {
    readonly Color ink = Color.FromArgb(28, 41, 58), accent = Color.FromArgb(12, 115, 100), muted = Color.FromArgb(97, 109, 124);
    readonly ComboBox server = new ComboBox(), mode = new ComboBox();
    readonly TextBox rules = new TextBox(), target = new TextBox(), result = new TextBox();
    readonly Label status = new Label(), statusDetail = new Label(), warning = new Label(), previewHint = new Label();
    readonly ListView routeList = new ListView();
    readonly List<Control> mutatingControls = new List<Control>();
    readonly Timer timer = new Timer();
    readonly TabControl tabs = new TabControl();
    Dictionary<string,object> plan;
    bool busy, refreshing, dirty, draftNeedsApply;
    string lastStatus = "NotConfigured";
    string ConfigFile { get { return Path.Combine(Program.StateDirectory, "draft.json"); } }

    public Client(bool render) {
        Text = "SJTU Link · v1.0.2 · 交大学生 VPN"; StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1000, 750); MinimumSize = new Size(1016, 789);
        Font = new Font("Microsoft YaHei UI", 10F); BackColor = Color.FromArgb(243,246,248); ForeColor = ink;
        AutoScaleMode = AutoScaleMode.Dpi;
        var header = new Panel { Dock = DockStyle.Top, Height = 114, BackColor = ink };
        header.Controls.Add(LabelAt("SJTU Link", 30, 9, 400, 58, 24, Color.White));
        header.Controls.Add(LabelAt("交大学生 VPN  /  IKEv2  /  独立于 aTrust", 32, 70, 860, 31, 10, Color.FromArgb(195,210,222)));
        Controls.Add(header);
        tabs.SetBounds(24, 134, 952, 587); tabs.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(tabs);
        var connectTab = Page("连接与分流"); var diagnosticTab = Page("出口诊断"); var aboutTab = Page("使用说明");
        // Let the status label measure its glyphs; a fixed height clipped Chinese text at higher DPI.
        status.Location=new Point(24,12); status.AutoSize=true; status.Font = new Font(Font.FontFamily,19,FontStyle.Bold); status.ForeColor = accent; status.Text = "尚未配置"; connectTab.Controls.Add(status);
        statusDetail.SetBounds(26,64,850,31); statusDetail.ForeColor = muted; statusDetail.Text = "先预览规则，再应用配置并登录。"; connectTab.Controls.Add(statusDetail);
        connectTab.Controls.Add(LabelAt("学生服务器", 26,100,150,24,10,ink));
        server.SetBounds(26,129,395,30); server.DropDownStyle = ComboBoxStyle.DropDownList;
        server.Items.AddRange(new object[] { "stu.vpn.sjtu.edu.cn", "stuv4.vpn.sjtu.edu.cn" }); server.SelectedIndex = 0; connectTab.Controls.Add(server);
        connectTab.Controls.Add(LabelAt("流量模式", 446,100,400,24,10,ink));
        mode.SetBounds(446,129,435,30); mode.DropDownStyle = ComboBoxStyle.DropDownList;
        mode.Items.AddRange(new object[] { "分流：仅以下目标走 VPN", "默认走 VPN：IPv4 + 全球单播 IPv6" }); mode.SelectedIndex = 0; connectTab.Controls.Add(mode);
        foreach (var combo in new [] { server, mode }) {
            combo.DrawMode=DrawMode.OwnerDrawFixed; combo.ItemHeight=25;
            combo.DrawItem += delegate(object sender, DrawItemEventArgs e) {
                var box=(ComboBox)sender; e.DrawBackground();
                if(e.Index>=0) TextRenderer.DrawText(e.Graphics,Convert.ToString(box.Items[e.Index]),box.Font,e.Bounds,e.ForeColor,TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                e.DrawFocusRectangle();
            };
        }
        // Each caption stays inside its column instead of painting over the adjacent heading.
        connectTab.Controls.Add(LabelAt("VPN 目标 · 每行一个域名、IP 或网段",26,179,395,25,10,ink));
        rules.SetBounds(26,211,395,158); rules.Multiline = true; rules.ScrollBars = ScrollBars.Vertical; rules.AcceptsReturn = true;
        rules.Font = new Font("Consolas",11); rules.Text = "net.sjtu.edu.cn"; connectTab.Controls.Add(rules);
        connectTab.Controls.Add(LabelAt("预览 · 将写入 VPN 的目标路由",446,179,435,25,10,ink));
        routeList.SetBounds(446,211,435,158); routeList.View = View.Details; routeList.FullRowSelect = true; routeList.GridLines = false;
        routeList.Columns.Add("规则来源",203); routeList.Columns.Add("目标网段 / IP",209); connectTab.Controls.Add(routeList);
        previewHint.SetBounds(26,381,855,42); previewHint.Font = new Font(Font.FontFamily,9); previewHint.ForeColor = muted;
        previewHint.Text = "域名按预览时的 DNS 结果转为 IP 路由，不含子域名。仅匹配这些 IP；共享 IP 的其他网站也受影响。"; connectTab.Controls.Add(previewHint);
        Button preview = ButtonAt(connectTab,"预览规则",26,429,132,() => Preview());
        Button apply = ButtonAt(connectTab,"应用配置",172,429,132,() => Apply());
        Button login = ButtonAt(connectTab,"登录并连接",318,429,170,() => Connect()); login.BackColor=accent; login.ForeColor=Color.White;
        ButtonAt(connectTab,"断开",502,429,102,() => Disconnect());
        ButtonAt(connectTab,"保存草稿",618,429,120,() => Save());
        ButtonAt(connectTab,"刷新状态",752,429,129,() => RefreshStatus());
        warning.SetBounds(26,483,855,60); warning.Font = new Font(Font.FontFamily,9); warning.ForeColor=Color.FromArgb(149,85,18); connectTab.Controls.Add(warning);
        mutatingControls.AddRange(new Control[] { server,mode,rules });
        diagnosticTab.Controls.Add(LabelAt("检查一个目标会使用哪条系统路由",26,22,850,35,17,ink));
        diagnosticTab.Controls.Add(LabelAt("这是 IP 路由诊断。系统代理、其他 VPN 和服务器策略可能改变实际访问结果。",26,69,855,50,10,muted));
        target.SetBounds(26,123,660,31); target.Text="net.sjtu.edu.cn"; diagnosticTab.Controls.Add(target);
        ButtonAt(diagnosticTab,"检查出口",706,119,174,() => Diagnose());
        result.SetBounds(26,178,854,290); result.Multiline=true; result.ReadOnly=true; result.ScrollBars=ScrollBars.Both; result.WordWrap=false;
        result.Font=new Font("Consolas",10); result.BackColor=Color.White; diagnosticTab.Controls.Add(result);
        ButtonAt(diagnosticTab,"查看学校 IP 检测页",26,487,222,() => { Process.Start("https://net.sjtu.edu.cn/"); return Task.FromResult(0); });
        ButtonAt(diagnosticTab,"打开 Windows VPN 设置",267,487,240,() => { Process.Start("ms-settings:network-vpn"); return Task.FromResult(0); });
        var help = new TextBox { Multiline=true,ReadOnly=true, BorderStyle=BorderStyle.None,BackColor=Color.White,ScrollBars=ScrollBars.Vertical };
        help.SetBounds(26,25,855,410); help.Text =
            "你的流量，由规则决定\r\n\r\n" +
            "1. 首次使用：先在 aTrust 中注销，再预览规则 → 应用配置 → 登录并连接。默认仅添加 net.sjtu.edu.cn 作为测试目标，不声称涵盖所有校园资源。\r\n\r\n" +
            "2. 登录由 Windows 系统窗口完成，输入 jAccount 用户名和密码。本工具不读取或记录密码。认证服务器证书提示请根据学校说明核对；没有关闭证书验证。\r\n\r\n" +
            "3. 分流模式：填写需要走 VPN 的准确域名或 IPv4/IPv6 网段。其他目标保留系统原有路径；代理和其他 VPN 仍可能影响它们。示例：某个校内域名、明确获知的服务器 IP 或网段。\r\n\r\n" +
            "4. 域名在点击预览时解析，应用使用这份预览结果。DNS 变化后需要断开、重新预览、应用并连接。未实现通配域名、按进程规则或自动 DNS 追踪。域名 DNS 查询沿用系统解析器，首次内网解析失败时可先用已知 IP，或切换默认 VPN 模式连接后解析。\r\n\r\n" +
            "5. 默认 VPN 模式为 Windows 配置 IPv4 默认隧道和 2000::/3 IPv6 路由，仍保留本地网络和更具体路由。并非流量隔离开关，VPN 断开后系统会恢复普通联网。\r\n\r\n" +
            "6. 改服务器或规则需要先断开。应用失败会尝试恢复此前配置。保存草稿不会修改网络。关闭窗口保留现有 VPN 连接，需要下线时点击断开。\r\n\r\n" +
            "7. stuv4.vpn.sjtu.edu.cn 仅以 IPv4 连接服务器，适用于排查 IPv6 连接问题；不等于 VPN 内部只能承载 IPv4。\r\n\r\n" +
            "个人客户端 · 非校方出品 · 使用系统 IKEv2，无需 aTrust 运行。学校标准 VPN 与 aTrust 的可访问资源是否完全相同，需要实际验证。";
        aboutTab.Controls.Add(help);
        ButtonAt(aboutTab,"删除本工具的 VPN 配置",26,467,270,() => Remove());
        ButtonAt(aboutTab,"学校官方配置说明",320,467,230,() => { Process.Start("https://net.sjtu.edu.cn/info/1200/3286.htm"); return Task.FromResult(0); });
        ButtonAt(aboutTab,"恢复旧连接管理",575,467,230,() => RecoverManagement());
        if (!render) LoadDraft();
        server.SelectedIndexChanged += delegate { InvalidatePlan(); }; mode.SelectedIndexChanged += delegate { InvalidatePlan(); }; rules.TextChanged += delegate { InvalidatePlan(); };
        timer.Interval=12000; timer.Tick += async delegate { if (!busy && !refreshing) await RefreshStatus(); };
        if (!render) Shown += async delegate { await Run(RefreshStatus); timer.Start(); };
        FormClosing += delegate(object sender, FormClosingEventArgs e) {
            if (busy) { e.Cancel=true; MessageBox.Show(this,"操作仍在进行，请等待完成或在系统登录窗口取消。",Text); }
            else if (dirty && MessageBox.Show(this,"有未保存的规则草稿。仍要关闭吗？",Text,MessageBoxButtons.YesNo,MessageBoxIcon.Question)==DialogResult.No) e.Cancel=true;
        };
    }
    TabPage Page(string title) { var page = new TabPage(title) { BackColor=Color.White,Padding=new Padding(0) }; tabs.TabPages.Add(page); return page; }
    Label LabelAt(string text,int x,int y,int w,int h,float size,Color color) { return new Label { Text=text,Location=new Point(x,y),Size=new Size(w,h),ForeColor=color,Font=new Font(Font.FontFamily,size) }; }
    Button ButtonAt(Control parent,string text,int x,int y,int w,Func<Task> action) {
        var b=new Button { Text=text,Location=new Point(x,y),Size=new Size(w,40),FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(239,244,245),Cursor=Cursors.Hand };
        b.FlatAppearance.BorderSize=0; b.Click += async delegate { await Run(action); }; parent.Controls.Add(b); mutatingControls.Add(b); return b;
    }
    async Task Run(Func<Task> action) {
        if (busy) return; busy=true; foreach(var c in mutatingControls)c.Enabled=false; UseWaitCursor=true;
        try { await action(); } catch(Exception e) { MessageBox.Show(this,e.Message,"SJTU Link · 操作未完成",MessageBoxButtons.OK,MessageBoxIcon.Warning); }
        finally { busy=false; foreach(var c in mutatingControls)c.Enabled=true; UseWaitCursor=false; }
    }
    void InvalidatePlan() { plan=null; routeList.Items.Clear(); dirty=true; draftNeedsApply=true; previewHint.Text="草稿已修改，尚未应用。请重新预览；域名会使用本次 DNS 解析结果。"; }
    object Draft(string action) { return new { Action=action,Server=server.Text,Mode=mode.SelectedIndex==0?"split":"full",Rules=rules.Lines,NeedsApply=draftNeedsApply }; }
    async Task Preview() {
        plan=(Dictionary<string,object>)await Program.Call(Draft("preview")); routeList.Items.Clear();
        foreach(var item in Program.Items(plan["Details"])) { var row=(Dictionary<string,object>)item; routeList.Items.Add(new ListViewItem(new string[]{Convert.ToString(row["Rule"]),Convert.ToString(row["Prefix"])})); }
        previewHint.Text="已预览，尚未应用 · " + plan["ResolvedAt"] + "。应用将使用以上 IP，域名变化后需重新预览并应用。";
    }
    async Task Apply() {
        if(plan==null) { await Preview(); MessageBox.Show(this,"预览已生成，请检查右侧路由，然后再次点击“应用配置”。",Text); return; }
        if(!await EnsureManagement())return;
        await Program.Call(new { Action="apply",Plan=plan });
        draftNeedsApply=false;
        previewHint.Text="配置已应用并校验。可以登录并连接。"; await Save(); await RefreshStatus();
    }
    async Task Connect() {
        var snapshot=(Dictionary<string,object>)await Program.Call(new { Action="status" });
        if (!(bool)snapshot["Exists"]) throw new Exception("请先预览并应用配置。");
        if(draftNeedsApply && MessageBox.Show(this,"草稿尚未应用。是否按现有已生效的 VPN 配置登录？\r\n本次不会应用草稿；要使用新规则，请取消后点击“应用配置”。",Text,MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
        if(Convert.ToString(snapshot["Status"])=="Connected") { await RefreshStatus(); return; }
        // Explicit user phonebook prevents the default RAS phonebook preference choosing another entry.
        string pbk=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),@"Microsoft\Network\Connections\Pbk\rasphone.pbk");
        status.Text="等待系统登录"; statusDetail.Text="在 Windows 窗口输入 jAccount 账号密码；可在该窗口取消。";
        var psi=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"rasphone.exe"),"-f \""+pbk+"\" -d \""+Program.Profile+"\"") { UseShellExecute=true };
        using(var process=Process.Start(psi)) { if(process!=null) await Task.Run(()=>process.WaitForExit()); }
        await RefreshStatus();
    }
    async Task Disconnect() {
        var psi=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"rasdial.exe"),"\""+Program.Profile+"\" /disconnect") { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
        using(var p=Process.Start(psi)) { var output=p.StandardOutput.ReadToEndAsync(); var error=p.StandardError.ReadToEndAsync(); await Task.Run(()=>p.WaitForExit()); if(p.ExitCode!=0)throw new Exception(await output + await error); }
        await RefreshStatus();
    }
    async Task RefreshStatus() {
        if(refreshing) return; refreshing=true;
        try {
            var s=(Dictionary<string,object>)await Program.Call(new { Action="status" }); lastStatus=Convert.ToString(s["Status"]);
            status.Text=lastStatus=="Connected"?"已连接":lastStatus=="Disconnected"?"已断开":lastStatus=="NotConfigured"?"尚未配置":"连接状态："+lastStatus;
            statusDetail.Text=(bool)s["Exists"] ? Convert.ToString(s["Server"])+"  ·  当前生效配置："+((bool)s["Split"]?"指定目标分流":"默认 VPN") : "先预览规则，再应用配置并登录。";
            warning.Text=string.Join("\r\n",Program.Items(s["Warnings"]).Select(Convert.ToString));
        } catch(Exception e) { status.Text="状态读取失败"; statusDetail.Text=e.Message; }
        finally { refreshing=false; }
    }
    Task Save() {
        Directory.CreateDirectory(Program.StateDirectory); string temp=ConfigFile+".tmp"; File.WriteAllText(temp,Program.Json.Serialize(Draft("draft")),new UTF8Encoding(false));
        if(File.Exists(ConfigFile))File.Replace(temp,ConfigFile,null); else File.Move(temp,ConfigFile); dirty=false; return Task.FromResult(0);
    }
    void LoadDraft() {
        if(!File.Exists(ConfigFile))return;
        try { var c=Program.Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(ConfigFile)); string saved=Convert.ToString(c["Server"]); if(server.Items.Contains(saved))server.SelectedItem=saved; mode.SelectedIndex=Convert.ToString(c["Mode"])=="full"?1:0; rules.Lines=Program.Items(c["Rules"]).Select(Convert.ToString).ToArray(); draftNeedsApply=!c.ContainsKey("NeedsApply") || (bool)c["NeedsApply"]; if(draftNeedsApply)previewHint.Text="已加载未应用的规则草稿。请预览并应用后连接。"; }
        catch { warning.Text="本地草稿无法读取，已使用默认值；原文件未被覆盖。"; }
    }
    async Task Diagnose() {
        result.Text="正在查询系统路由…"; var d=(Dictionary<string,object>)await Program.Call(new { Action="diagnose",Target=target.Text.Trim() });
        var text=new StringBuilder(); text.AppendLine("目标："+d["Target"]); text.AppendLine();
        foreach(var entry in Program.Items(d["Results"])) {
            var row=(Dictionary<string,object>)entry; text.AppendLine(Convert.ToString(row["Address"]));
            if(row.ContainsKey("Error"))text.AppendLine("  "+row["Error"]);
            else foreach(var selection in Program.Items(row["Selection"])) { var s=(Dictionary<string,object>)selection; foreach(var pair in s) if(pair.Value!=null)text.AppendLine("  "+pair.Key+": "+pair.Value); }
            text.AppendLine();
        }
        text.AppendLine(Convert.ToString(d["Notice"])); result.Text=text.ToString();
    }
    async Task Remove() {
        if(!await EnsureManagement())return;
        if(MessageBox.Show(this,"删除本工具创建的 Windows VPN 连接和其中的路由？本地规则草稿会保留。",Text,MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
        await Program.Call(new { Action="remove" }); plan=null; routeList.Items.Clear(); await RefreshStatus();
    }
    async Task<bool> EnsureManagement() {
        var s=(Dictionary<string,object>)await Program.Call(new {Action="status"});
        if(!(bool)s["Exists"] || Convert.ToString(s["Ownership"])=="matched")return true;
        string reason=Convert.ToString(s["Ownership"]);
        string explanation=reason=="missing"?"本地管理标记缺失":reason=="unreadable"?"本地管理标记无法读取":reason=="invalid"?"本地管理标记格式无效":"本地管理标记与连接不匹配";
        if(MessageBox.Show(this,explanation+"。\r\n\r\n是否由本工具管理已有连接？\r\n"+Program.Profile+"\r\n服务器："+s["Server"]+"\r\n\r\n恢复仅保存管理记录，不重建连接、不重置认证或路由。随后才执行你选择的操作。",Text,MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return false;
        await Program.Call(new {Action="restore-owner",ProfileId=s["ProfileId"],Server=s["Server"]});
        return true;
    }
    async Task RecoverManagement() { if(await EnsureManagement()) { await RefreshStatus(); MessageBox.Show(this,"管理记录已就绪，可以应用配置。",Text); } }
}
