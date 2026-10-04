using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Bindings.ImGui;
using Mahjong.Plugin.CN;
using Mahjong.Plugin.CN.Access;
using Mahjong.Plugin.CN.Presentation;
using Mahjong.Plugin.CN.Ui;

// Real production Draw(), isolated synthetic plugin state, software rasterization of
// native ImGui draw data. No game process, native input, framework services or network.
unsafe class Program
{
    static void Set(object obj,string name,object? value)=>obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(obj,value);
    static void Property(object obj,string name,object? value)=>Set(obj,"<"+name+">k__BackingField",value);
    static void Main(string[] args)
    {
        if (args is ["--framework-contract", var contractPath])
        {
            File.WriteAllText(contractPath, System.Text.Json.JsonSerializer.Serialize(
                FrameworkCompatibility.CaptureRequired(typeof(Plugin).Assembly),
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        string output=Path.GetFullPath(args.Length>0?args[0]:"artifacts/ui-host");Directory.CreateDirectory(output);
        NativeLibrary.SetDllImportResolver(typeof(ImGui).Assembly,(name,_,_)=>NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory,name.EndsWith(".dll")?name:name+".dll")));
        var context=ImGui.CreateContext();var io=ImGui.GetIO();io.IniFilename=(byte*)0;
        io.Fonts.AddFontFromFileTTF(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts),"msyh.ttc"),18,null,io.Fonts.GetGlyphRangesChineseFull());
        io.Fonts.Build();byte* pixels;int tw,th;io.Fonts.GetTexDataAsRGBA32(0,&pixels,&tw,&th);io.Fonts.SetTexID(0,(ImTextureID)1);
        var access=new TestCodeAccess(Path.Combine(output,"absent-access-"+Guid.NewGuid().ToString("N")+".json"),null,SHA256.HashData(Encoding.UTF8.GetBytes("synthetic-host")));
        if(access.IsUnlocked) throw new InvalidOperationException("Host must exercise the unverified standard experience");
        var plugin=(Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        Set(plugin,"testAccess",access);Set(plugin,"taskRun",new Mahjong.Cn.Tasks.TaskRun());Set(plugin,"uiSnapshot",PluginUiSnapshot.Empty);
        Set(plugin,"tableAutomation",new Mahjong.Plugin.CN.Automation.TableAutomation());
        Set(plugin,"stopRecorder",new Mahjong.Plugin.CN.Diagnostics.GameplayStopRecorder(output));
        Property(plugin,"ExportStatus","测试宿主：未导出。");Property(plugin,"JournalKeepMatches",50);Set(plugin,"journalMaintenanceStatus","测试宿主：不修改日志。");
        Set(plugin,"history",new Plugin.HistoryState([],"测试宿主：无真实记录。"));
        Property(plugin,"ResultsStatus","测试宿主：暂无待保存结果。");
        Property(plugin,"QuickRecoveryStatus","短暂读取中断后重新核对同桌；原任务权限有效才继续。");
        Property(plugin,"RetryTransientErrors",true);
        Property(plugin,"CorpusExportStatus","测试宿主：固定样本只在本机导出。");
        Set(plugin,"networkPreferences",new Plugin.NetworkPreferences());Set(plugin,"community",new Plugin.CommunityState(new(null,null,null),null,null,"测试宿主：联网关闭。"));
        Property(plugin,"Identity",new RuntimeIdentity("synthetic",15,"synthetic","","","Chinese","host",null));
        Property(plugin,"Status","测试宿主：未连接游戏，所有读数均未知。");
        Property(plugin,"AutomationOptions",new Mahjong.Plugin.CN.Automation.TableAutomationOptions());
        Property(plugin,"TaskRules",new Mahjong.Cn.Tasks.StopRuleSet());Property(plugin,"UiEngines",Array.Empty<Plugin.EngineUiItem>());
        Property(plugin,"TaskSettingsStatus","测试宿主：未启动。");Property(plugin,"RecoveryStatus","测试宿主：无恢复数据。");
        Property(plugin,"EngineInstallationStatus","测试宿主：模型未安装。");
        Property(plugin,"UiEngineDirectory","synthetic-not-installed");
        Property(plugin,"BetaAccessStatus","测试宿主：未验证。标准功能不受影响。");
        GlassTheme.Initialize(output);
        var window=new MainWindow(plugin);
        foreach(float scale in new[]{1f,1.5f,2f})
        foreach(int logicalWidth in new[]{1240,980,620,420})
        foreach(int page in logicalWidth==980?new[]{0,1,2,3,4}:new[]{0})
        {
            int width=(int)(logicalWidth*scale),height=(int)((logicalWidth==420?360:760)*scale);
            io.DisplaySize=new(width,height);io.FontGlobalScale=scale;io.DeltaTime=1f/60;
            Set(window,"compact",logicalWidth==420);
            Set(window,"page",page);
            for(int frame=0;frame<3;frame++)
            {
                ImGui.NewFrame();window.PreDraw();ImGui.SetNextWindowPos(Vector2.Zero);ImGui.SetNextWindowSize(new(width,height));
                ImGui.Begin("MahjongSolver · 未验证 / 无模型 · 测试宿主（非实机）",ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoResize);
                window.Draw();ImGui.End();window.PostDraw();ImGui.Render();
            }
            Raster.Save(ImGui.GetDrawData(),pixels,tw,th,width,height,Path.Combine(output,$"page-{page}-{logicalWidth}-{scale*100:0}.png"));
        }
        if(plugin.TestAccessUnlocked || plugin.ExperimentalHandAiEnabled || plugin.PlayRuntime is not null ||
            plugin.GameOperationsAvailable || plugin.GameOperationsAuthorized || plugin.TableAutomationArmed || plugin.RatingRefreshBusy)
            throw new InvalidOperationException("Unverified rendering changed access or started play/operations");
        // Deliberately synthetic review row, rendered by the real production history page.
        Set(plugin,"history",new Plugin.HistoryState([new Mahjong.Plugin.CN.Journaling.MatchSummary(Guid.NewGuid(),DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(30),766,"upstream-efficiency","fixture","完成","TEST_FIXTURE", "synthetic")
            {IntegrityVerified=true,ConfigurationFingerprint=new string('a',64),RecordedTakeovers=0,Submissions=10,ObservedTransitions=9,ObservationTimeouts=1}],
            "纯合成测试样例，非真实战绩。最终名次与评分未知。"));
        Set(plugin,"review",new Plugin.ReviewState(null,new([],"合成测试宿主。")));
        foreach(float scale in new[]{1f,1.5f,2f})
        foreach(int logicalWidth in new[]{980,620})
        {
            int width=(int)(logicalWidth*scale),height=(int)(760*scale);io.DisplaySize=new(width,height);io.FontGlobalScale=scale;
            Set(window,"compact",false);Set(window,"page",2);
            for(int frame=0;frame<3;frame++)
            {
                ImGui.NewFrame();window.PreDraw();ImGui.SetNextWindowPos(Vector2.Zero);ImGui.SetNextWindowSize(new(width,height));
                ImGui.Begin("SYNTHETIC REVIEW FIXTURE - NOT LIVE GAME",ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoResize);
                window.Draw();ImGui.End();window.PostDraw();ImGui.Render();
            }
            Raster.Save(ImGui.GetDrawData(),pixels,tw,th,width,height,Path.Combine(output,$"review-fixture-{logicalWidth}-{scale*100:0}.png"));
        }
        // Qualification alone exposes all controls but still cannot start a backend/task.
        if (!access.TryUnlock("synthetic-host")) throw new InvalidOperationException("Synthetic lease failed");
        Property(plugin,"BetaAccessStatus","测试宿主：资格有效，尚未启动。");
        foreach(float scale in new[]{1f,1.5f,2f})
        foreach(int logicalWidth in new[]{980,620})
        foreach(int qualifiedPage in new[]{0,1,3})
        {
            int width=(int)(logicalWidth*scale),height=(int)(760*scale);io.DisplaySize=new(width,height);io.FontGlobalScale=scale;
            Set(window,"compact",false);Set(window,"page",qualifiedPage);
            for(int frame=0;frame<3;frame++)
            {
                ImGui.NewFrame();window.PreDraw();ImGui.SetNextWindowPos(Vector2.Zero);ImGui.SetNextWindowSize(new(width,height));
                ImGui.Begin("MahjongSolver · 资格有效 / 无模型 · 测试宿主（非实机）",ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoResize);
                window.Draw();ImGui.End();window.PostDraw();ImGui.Render();
            }
            Raster.Save(ImGui.GetDrawData(),pixels,tw,th,width,height,Path.Combine(output,$"qualified-{qualifiedPage}-{logicalWidth}-{scale*100:0}.png"));
        }
        if (!plugin.GameOperationsAvailable || plugin.ExperimentalHandAiEnabled || plugin.PlayRuntime is not null ||
            plugin.GameOperationsAuthorized || plugin.TableAutomationArmed || plugin.RatingRefreshBusy)
            throw new InvalidOperationException("Qualification rendering started a task/backend or hid availability");
        // Explicitly synthetic states: UI coverage only, no task authority or model execution.
        Set(plugin,"experimentalHandAiEnabled",1);
        Property(plugin,"AutomationOptions",new Mahjong.Plugin.CN.Automation.TableAutomationOptions(SecondaryDutyId:643));
        foreach(float scale in new[]{1f,1.5f,2f})
        foreach(var state in new[]{"preparing","recovery","paused","dual-queue"})
        {
            var status=state switch
            {
                "preparing"=>new RunPresentation("正在准备模型","后台校验与首次推理中；等待就绪后再报名。"),
                "recovery"=>new RunPresentation("正在恢复","重新核对同一牌桌。暂停可取消恢复。"),
                "paused"=>new RunPresentation("已暂停","提醒、出牌与排队均已暂停；模型留在后台，进度保留。"),
                _=>new RunPresentation("尚未开始","合成双桌型设置；没有实际报名。"),
            };
            Set(plugin,"uiSnapshot",PluginUiSnapshot.Empty with {Engine="凡夫 Mortal V4（测试版）",RunStatus=status});
            int width=(int)(620*scale),height=(int)(760*scale);io.DisplaySize=new(width,height);io.FontGlobalScale=scale;
            Set(window,"compact",false);Set(window,"page",state=="dual-queue"?1:0);
            for(int frame=0;frame<3;frame++)
            {
                ImGui.NewFrame();window.PreDraw();ImGui.SetNextWindowPos(Vector2.Zero);ImGui.SetNextWindowSize(new(width,height));
                ImGui.Begin("SYNTHETIC STATUS - NOT LIVE GAME",ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoResize);
                window.Draw();ImGui.End();window.PostDraw();ImGui.Render();
            }
            Raster.Save(ImGui.GetDrawData(),pixels,tw,th,width,height,Path.Combine(output,$"status-{state}-{scale*100:0}.png"));
        }
        Set(plugin,"uiSnapshot",PluginUiSnapshot.Empty);
        Set(plugin,"experimentalHandAiEnabled",0);
        Property(plugin,"AutomationOptions",new Mahjong.Plugin.CN.Automation.TableAutomationOptions());
        Set(window,"compact",false);Set(window,"page",0);io.FontGlobalScale=1;io.DisplaySize=new(980,680);
        var appearance=(GlassTheme.Appearance)typeof(GlassTheme).GetField("options",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
        var measurements=new List<object>();
        foreach(bool modern in new[]{false,true})
        {
            appearance.ModernLayout=modern;long allocated=GC.GetAllocatedBytesForCurrentThread();var timer=System.Diagnostics.Stopwatch.StartNew();
            for(int frame=0;frame<120;frame++)
            {ImGui.NewFrame();window.PreDraw();ImGui.SetNextWindowSize(new(980,680));ImGui.Begin("TEST HOST CPU ONLY");window.Draw();ImGui.End();window.PostDraw();ImGui.Render();}
            timer.Stop();measurements.Add(new{Layout=modern?"modern":"classic",Frames=120,MillisecondsPerFrame=timer.Elapsed.TotalMilliseconds/120,ManagedBytesPerFrame=(GC.GetAllocatedBytesForCurrentThread()-allocated)/120,Scope="Synthetic empty state; no GPU/game benchmark"});
        }
        File.WriteAllText(Path.Combine(output,"host-timing.json"),System.Text.Json.JsonSerializer.Serialize(measurements));
        ImGui.DestroyContext(context);
        Console.WriteLine("Rendered production overview and compact Draw at 100/150/200%. Synthetic state; not CN live verification.");
    }
}

static unsafe class Raster
{
    internal static void Save(ImDrawDataPtr data,byte* texture,int tw,int th,int width,int height,string path)
    {
        byte[] canvas=new byte[width*height*4];for(int i=0;i<canvas.Length;i+=4){canvas[i]=42;canvas[i+1]=36;canvas[i+2]=28;canvas[i+3]=255;}
        for(int l=0;l<data.CmdListsCount;l++)
        {
            var list=new ImDrawListPtr(data.CmdLists[l]);
            foreach(var command in list.CmdBuffer)
            {
                if(command.UserCallback!=null)continue;
                for(uint e=0;e<command.ElemCount;e+=3)
                {
                    var a=list.VtxBuffer[(int)(list.IdxBuffer[(int)(command.IdxOffset+e)]+command.VtxOffset)];
                    var b=list.VtxBuffer[(int)(list.IdxBuffer[(int)(command.IdxOffset+e+1)]+command.VtxOffset)];
                    var c=list.VtxBuffer[(int)(list.IdxBuffer[(int)(command.IdxOffset+e+2)]+command.VtxOffset)];
                    float area=Cross(b.Pos-a.Pos,c.Pos-a.Pos);if(Math.Abs(area)<.001)continue;
                    int x0=Math.Max(0,(int)Math.Max(command.ClipRect.X,MathF.Floor(Math.Min(a.Pos.X,Math.Min(b.Pos.X,c.Pos.X)))));
                    int y0=Math.Max(0,(int)Math.Max(command.ClipRect.Y,MathF.Floor(Math.Min(a.Pos.Y,Math.Min(b.Pos.Y,c.Pos.Y)))));
                    int x1=Math.Min(width,(int)Math.Min(command.ClipRect.Z,MathF.Ceiling(Math.Max(a.Pos.X,Math.Max(b.Pos.X,c.Pos.X)))));
                    int y1=Math.Min(height,(int)Math.Min(command.ClipRect.W,MathF.Ceiling(Math.Max(a.Pos.Y,Math.Max(b.Pos.Y,c.Pos.Y)))));
                    for(int y=y0;y<y1;y++)for(int x=x0;x<x1;x++)
                    {
                        var p=new Vector2(x+.5f,y+.5f);float wa=Cross(b.Pos-p,c.Pos-p)/area,wb=Cross(c.Pos-p,a.Pos-p)/area,wc=1-wa-wb;
                        if(wa<0||wb<0||wc<0)continue;
                        var uv=a.Uv*wa+b.Uv*wb+c.Uv*wc;int tx=Math.Clamp((int)(uv.X*tw),0,tw-1),ty=Math.Clamp((int)(uv.Y*th),0,th-1);
                        int ti=(ty*tw+tx)*4,ci=(y*width+x)*4;
                        float alpha=texture[ti+3]/255f*(Channel(a.Col,24)*wa+Channel(b.Col,24)*wb+Channel(c.Col,24)*wc)/255f;
                        for(int ch=0;ch<3;ch++)
                        {float color=(Channel(a.Col,ch*8)*wa+Channel(b.Col,ch*8)*wb+Channel(c.Col,ch*8)*wc)*texture[ti+ch]/255f;int target=ci+2-ch;canvas[target]=(byte)Math.Clamp(color*alpha+canvas[target]*(1-alpha),0,255);}
                    }
                }
            }
        }
        using var bitmap=new System.Drawing.Bitmap(width,height,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bits=bitmap.LockBits(new(0,0,width,height),System.Drawing.Imaging.ImageLockMode.WriteOnly,bitmap.PixelFormat);
        try{Marshal.Copy(canvas,0,bits.Scan0,canvas.Length);}finally{bitmap.UnlockBits(bits);}
        bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png);
    }
    static float Cross(Vector2 a,Vector2 b)=>a.X*b.Y-a.Y*b.X;
    static int Channel(uint color,int shift)=>(int)(color>>shift)&255;
}
