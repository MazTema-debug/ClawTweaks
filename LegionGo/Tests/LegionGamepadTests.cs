// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using System.Threading.Tasks;
using LegionTweaks;

internal static partial class Program
{
    private static byte[] DirectPadReport()
    {
        var packet = new byte[64]; packet[0] = 4; packet[1] = 0x3c; packet[2] = 0x74;
        for (int i = 14; i <= 17; i++) packet[i] = 128;
        return packet;
    }
    private static void LegionGamepadTests()
    {
        // Synthetic wire facts; no private hardware captures are distributed.
        var buttonCases = new[] {
            new[] {18,0x20,64}, new[] {18,0x10,128}, new[] {18,8,1}, new[] {18,4,2}, new[] {18,2,4}, new[] {18,1,8},
            new[] {19,0x80,4096}, new[] {19,0x40,8192}, new[] {19,0x20,16384}, new[] {19,0x10,32768},
            new[] {19,8,256}, new[] {19,2,512}, new[] {20,2,32}, new[] {20,1,16}
        };
        foreach (var item in buttonCases) Test("Direct HID standard button " + item[2] + " remains independent", () => {
            var report = DirectPadReport(); report[item[0]] = (byte)item[1]; GamepadFrame frame;
            Assert(LegionGamepadReport.TryDecode(report, out frame) && frame.Buttons == item[2]
                && frame.LT == 0 && frame.RT == 0 && frame.LegionButtons == 0, "wrong button or crossed trigger/auxiliary");
        });
        Test("Direct HID preserves analog trigger amounts independently of digital trigger flags", () => {
            foreach (byte left in new byte[] {0,1,63,128,254,255}) foreach (byte right in new byte[] {0,1,127,255}) {
                var packet = DirectPadReport(); packet[19] = 5; packet[22] = left; packet[23] = right; GamepadFrame frame;
                Assert(LegionGamepadReport.TryDecode(packet, out frame) && frame.LT == left && frame.RT == right && frame.Buttons == 0,
                    "triggers were quantized, combined or turned into buttons");
            }
        });
        Test("Direct HID separates and orients all four axes with exact neutral and full endpoints", () => {
            for (int axis = 0; axis < 4; axis++) foreach (byte raw in new byte[] {0,128,255}) {
                var packet = DirectPadReport(); packet[14+axis] = raw; GamepadFrame frame;
                Assert(LegionGamepadReport.TryDecode(packet,out frame), "valid packet rejected");
                var values = new[] {frame.LX,frame.LY,frame.RX,frame.RY};
                short expected = raw == 128 ? (short)0 : (raw == 0) == (axis % 2 == 0) ? short.MinValue : short.MaxValue;
                Assert(values[axis] == expected && values.Where((v,i) => i != axis).All(v => v == 0), "axis crossed, reversed or missing full range");
            }
        });
        Test("Direct HID axis conversion is monotonic through both halves of center", () => {
            short previous = short.MinValue;
            for (int raw = 0; raw < 256; raw++) {
                var packet = DirectPadReport(); packet[14] = (byte)raw; GamepadFrame frame;
                Assert(LegionGamepadReport.TryDecode(packet,out frame) && frame.LX >= previous, "axis wrapped or reversed near neutral"); previous=frame.LX;
            }
        });
        Test("Direct HID rejects incomplete, unknown and command-response formats", () => {
            foreach (var packet in new[] {null,new byte[63],new byte[65],new byte[64]}) { GamepadFrame frame; Assert(!LegionGamepadReport.TryDecode(packet,out frame),"invalid packet accepted"); }
            foreach (var header in new[] {new byte[]{4,0,0xa2},new byte[]{4,0,3},new byte[]{5,0x3c,0x74},new byte[]{4,0x3c,0x75}}) {
                var packet=DirectPadReport(); Array.Copy(header,packet,3); GamepadFrame frame;
                Assert(!LegionGamepadReport.TryDecode(packet,out frame),"an unverified format supplied gameplay input");
            }
        });
        Test("The legacy original-Go report shifts its standard payload by two bytes without reading adjacent metadata", () => {
            foreach(var item in buttonCases) {
                var packet=new byte[64];packet[0]=4;packet[2]=0xa1;
                for(int i=12;i<16;i++) packet[i]=128;
                packet[item[0]-2]=(byte)item[1];packet[20]=37;packet[21]=129;packet[22]=255;packet[23]=255;
                GamepadFrame frame;Assert(LegionGamepadReport.TryDecode(packet,out frame) && frame.Buttons==item[2]
                    && frame.LT==37 && frame.RT==129 && frame.LX==0 && frame.LY==0 && frame.RX==0 && frame.RY==0,"legacy payload used the other format's offsets");
            }
        });
        Test("Direct HID does not turn auxiliary, touch or IMU data into standard buttons", () => {
            var packet=DirectPadReport(); packet[18]=0xc0; packet[20]=0xfc;
            for(int i=24;i<64;i++) packet[i]=255;
            GamepadFrame frame; Assert(LegionGamepadReport.TryDecode(packet,out frame) && frame.Buttons==0 && frame.LegionButtons==0,"extra data leaked into buttons");
        });
        Test("Direct HID cache expires held input and never revives it after a clock reversal", () => {
            var cache=new LegionGamepadCache(); var packet=DirectPadReport(); packet[19]=0x80; cache.Observe(packet,100);
            GamepadFrame frame; Assert(cache.TryRead(350,out frame) && frame.Buttons==4096,"fresh held input missing");
            Assert(!cache.TryRead(351,out frame) && frame.Buttons==0 && !cache.TryRead(100,out frame),"expired input replayed");
            cache.Observe(packet,500); Assert(!cache.TryRead(499,out frame) && !cache.TryRead(500,out frame),"clock reversal replayed a button");
            cache.Observe(packet,600); cache.Reset(); Assert(!cache.TryRead(600,out frame),"reset retained held input");
        });
        Test("Game Bar input changes require release on entry, exit and missing source recovery", () => {
            var gate=new ControllerInputRouteGate(); var layout=new ControllerLayout(); var held=new GamepadFrame{Buttons=4096};
            Assert(gate.Accept(false,true,held,layout),"ordinary SDL input was changed");
            Assert(!gate.Accept(true,true,held,layout) && gate.WaitingForNeutral,"opening Game Bar replayed activation input");
            Assert(gate.Accept(true,true,new GamepadFrame{LX=700,RY=-900},layout),"normal stick wear prevented neutral");
            Assert(gate.Accept(true,true,held,layout),"new input was not accepted after release");
            Assert(!gate.Accept(false,true,held,layout),"closing Game Bar leaked a held button into the game");
            Assert(gate.Accept(false,true,new GamepadFrame(),layout,new GamepadFrame()),"SDL did not rearm");
            Assert(!gate.Accept(true,false,new GamepadFrame(),layout) && !gate.Accept(true,true,held,layout),"missing HID input became an old press");
        });
        Test("A focus-filtered SDL zero cannot release a still-held Game Bar button", () => {
            var route=new ControllerInputRouteGate();var layout=new ControllerLayout();var released=new GamepadFrame();var held=new GamepadFrame{Buttons=8192};
            Assert(route.Accept(true,true,released,layout),"direct route failed to arm");
            Assert(route.NeedsDirectNeutral(false),"leaving route did not require physical release");
            Assert(!route.Accept(false,true,released,layout,held),"filtered zero falsely released physical B");
            Assert(!route.Accept(false,true,released,layout),"missing evidence falsely released B");
            Assert(!route.Accept(false,true,held,layout,released),"old SDL held state leaked through after physical release");
            Assert(route.Accept(false,true,released,layout,released) && !route.NeedsDirectNeutral(false),"released routes did not rearm");
        });
        Test("Each Game Bar view owns an expiring observation and delayed activation cannot undo closing", () => {
            long now=DateTime.UtcNow.Ticks; string a=Guid.NewGuid().ToString("N"),b=Guid.NewGuid().ToString("N"); var leases=new GameBarInputLease();
            leases.Observe(new GameBarInputContext{Owner=a,Foreground=true,ObservedUtc=now},now);
            leases.Observe(new GameBarInputContext{Owner=b,Foreground=false,ObservedUtc=now},now);
            Assert(leases.Active(now),"another view cleared the foreground widget");
            leases.Observe(new GameBarInputContext{Owner=a,Foreground=false,ObservedUtc=now+1},now+1);
            leases.Observe(new GameBarInputContext{Owner=a,Foreground=true,ObservedUtc=now},now+2);
            Assert(!leases.Active(now+2),"a delayed open reactivated a closed view");
            leases.Observe(new GameBarInputContext{Owner=b,Foreground=true,ObservedUtc=now+3},now+3);
            Assert(!leases.Active(now+TimeSpan.FromSeconds(5).Ticks),"abandoned widget never expired");
            leases.Observe(new GameBarInputContext{Owner=b,Foreground=true,ObservedUtc=now},now+TimeSpan.FromSeconds(10).Ticks);
            Assert(!leases.Active(now+TimeSpan.FromSeconds(10).Ticks),"queued old observation renewed input");
            leases.Reset(); Assert(!leases.Active(now),"suspension retained the foreground lease");
        });
        Test("A same-timestamp Game Bar close dominates open regardless of delivery order", () => {
            long now=DateTime.UtcNow.Ticks; string id=Guid.NewGuid().ToString("N"); var leases=new GameBarInputLease();
            leases.Observe(new GameBarInputContext{Owner=id,Foreground=true,ObservedUtc=now},now);
            leases.Observe(new GameBarInputContext{Owner=id,Foreground=false,ObservedUtc=now},now);
            leases.Observe(new GameBarInputContext{Owner=id,Foreground=true,ObservedUtc=now},now);
            Assert(!leases.Active(now),"closed view was reopened by the same observation");
        });
        Test("Optional widget context is copied into its status request without acquiring a command operation", () => {
            Request seen=null; var channel=new RequestChannel((r,budget)=>{seen=r;return Task.FromResult(new Response{Success=true});});
            var context=new GameBarInputContext{Owner=Guid.NewGuid().ToString("N"),Foreground=true,ObservedUtc=DateTime.UtcNow.Ticks};
            Assert(channel.TryReadStatus(context).GetAwaiter().GetResult().Success,"status failed");
            context.Foreground=false;
            Assert(seen.Operation=="recording_status" && seen.GameBarInput.Foreground && seen.Profile==null && seen.Values==null,"caller mutated a queued observation or applied a profile");
            channel.TryReadStatus().GetAwaiter().GetResult(); Assert(seen.GameBarInput==null,"desktop status inherited widget context");
        });
        Test("A lifecycle close queues behind an ongoing read without changing its observation time", () => {
            var blocked=new TaskCompletionSource<Response>(); Request closed=null; int calls=0;
            var channel=new RequestChannel((r,budget)=>{ if(++calls==1) return blocked.Task; closed=r; return Task.FromResult(new Response{Success=true}); });
            var read=channel.TryReadStatus(); long observed=DateTime.UtcNow.Ticks;
            var close=channel.Send(new Request{Operation="recording_status",GameBarInput=new GameBarInputContext{
                Owner=Guid.NewGuid().ToString("N"),Foreground=false,ObservedUtc=observed}});
            Assert(!close.IsCompleted && calls==1,"lifecycle event competed with an active pipe request");
            blocked.SetResult(new Response{Success=true}); read.GetAwaiter().GetResult(); close.GetAwaiter().GetResult();
            Assert(closed.GameBarInput!=null && !closed.GameBarInput.Foreground && closed.GameBarInput.ObservedUtc==observed,"close dropped or observation time renewed");
        });
        Test("Standard HID and auxiliary consumers share one reader and capture never forwards held gameplay input", () => WithLegionButtonDirectory(folder => {
            using(var bus=new FakeLegionButtonBus()) using(var service=bus.CreateService(folder)) {
                service.SetGamepadInputEnabled(true); LegionButtonWait(()=>bus.Active==1,"standard reader missing");
                service.SetControllerInputEnabled(true);
                var packet=DirectPadReport(); packet[19]=0x80; bus.PushReport(service,0,packet);
                GamepadFrame frame; Assert(service.TryReadGamepad(out frame) && frame.Buttons==4096,"standard input was unavailable without auxiliary mappings");
                service.StartCapture(2,1); Assert(service.GamepadInputPaused && !service.TryReadGamepad(out frame),"capture leaked input or was mistaken for a failed source");
                service.CancelCapture(); Assert(!service.GamepadInputPaused && !service.TryReadGamepad(out frame),"capture cancel revived old input");
                bus.PushReport(service,100,packet); Assert(service.TryReadGamepad(out frame),"reader did not resume");
                service.SetPreparationPaused(true); Assert(!service.TryReadGamepad(out frame) && bus.Active==0,"preparation retained its reader or held input");
                service.SetPreparationPaused(false); LegionButtonWait(()=>bus.Active==1,"reader did not reopen after preparation");
                Assert(!service.TryReadGamepad(out frame),"reopen replayed old input");
                bus.PushReport(service,200,packet); service.Suspend(true); Assert(!service.TryReadGamepad(out frame),"suspension kept held input");
                service.Suspend(false); LegionButtonWait(()=>bus.Active==1,"reader did not resume after suspension");
                service.SetControllerInputEnabled(false); service.SetGamepadInputEnabled(false);
                LegionButtonWait(()=>bus.Active==0,"stopped controller retained an unneeded reader");
                Assert(bus.MaximumActive==1 && !service.TryReadGamepad(out frame),"duplicate reader or stopped input");
            }
        }));
        Test("Game Bar status context changes no energy, profile or controller activation", () => WithRecordingDirectory(folder => {
            var hardware=new Fake(); var journal=new Journal(); var controller=new InputContextTestController();
            using(var session=new HelperSession(new ControlEngine(hardware,journal),folder,false,controller:controller)) {
                var context=new GameBarInputContext{Owner=Guid.NewGuid().ToString("N"),ObservedUtc=DateTime.UtcNow.Ticks,Foreground=true};
                Assert(session.Execute(new Request{Operation="recording_status",GameBarInput=context}).Success && controller.Observations==1,"context missing");
                Assert(hardware.Writes.Count==0 && journal.Saves==0 && controller.Applies==0,"status context activated or applied settings");
                Assert(!session.Execute(new Request{Operation="controller_desktop",GameBarInput=context}).Success && controller.Observations==1,"context reached another command");
                Assert(!session.Execute(new Request{Operation="recording_status",GameBarInput=context,Values=new[]{1}}).Success && controller.Observations==1,"context accepted hidden settings");
            }
        }));
        Test("A failed native read with no error code cannot report successful completion", () => {
            var error=NativeReadFailure.Create("leer el descriptor HID",0);
            Assert(error.NativeErrorCode==0 && error.Message.Contains("no proporcionó") && !error.Message.Contains("completó correctamente"),"ambiguous native failure reported success");
            Assert(NativeReadFailure.Create("abrir el lector",5).NativeErrorCode==5,"native error was lost");
        });
        Test("Preparation failure evidence retains code origin without messages or source paths", () => {
            Exception error=null;
            try { FailureOriginFixture.ThrowFailure(); } catch(Exception captured) { error=captured; }
            var failure=ControllerPreparationFailure.Capture(error,Guid.NewGuid().ToString("N"),123);
            string encoded=Protocol.Encode(failure);
            Assert(failure.ExceptionType=="NullReferenceException" && failure.Methods.Any(m=>m.Contains("FailureOriginFixture.ThrowFailure")),"failure location missing");
            Assert(!encoded.Contains("private-account") && !encoded.Contains("secret-device") && !encoded.Contains(".cs:"),"exception message or source path leaked");
        });
        Test("Failure evidence keeps an actual Windows error code without its private message", () => {
            var failure=ControllerPreparationFailure.Capture(new System.ComponentModel.Win32Exception(5,"secret-device"),Guid.NewGuid().ToString("N"),123);
            Assert(failure.NativeCode==5 && !Protocol.Encode(failure).Contains("secret-device"),"native evidence retained its raw message");
        });
    }
    private sealed class InputContextTestController:IControllerService,IGameBarControllerInput
    {
        internal int Applies,Observations;
        public ControllerLayout Layout=>new ControllerLayout();
        public ControllerStatus GetStatus()=>new ControllerStatus();
        public void Preflight(ControllerLayout layout) { }
        public void Apply(ControllerLayout layout) { Applies++; }
        public void ObserveGameBarInput(GameBarInputContext context) { Observations++; }
        public void Dispose() { }
    }
}

namespace LegionTweaks
{
    internal static class FailureOriginFixture
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void ThrowFailure() { throw new NullReferenceException(@"C:\Users\private-account\secret-device"); }
    }
}
