using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
internal static class Smoke {
    static void Main() {
        var task=Program.Call(new { Action="preview",Server="stu.vpn.sjtu.edu.cn",Mode="split",Rules=new string[]{"202.120.2.100","2001:db8::123/48"} });
        var data=(Dictionary<string,object>)task.GetAwaiter().GetResult();
        Console.WriteLine("Routes type: "+data["Routes"].GetType().FullName);
        var routes=((IEnumerable)data["Routes"]).Cast<object>().Select(Convert.ToString).ToArray();
        if(!routes.Contains("202.120.2.100/32") || !routes.Contains("2001:db8::/48"))throw new Exception("Unexpected route plan");
        Console.WriteLine("PASS: executable -> embedded PowerShell -> JSON roundtrip, IPv4 and IPv6.");
    }
}
