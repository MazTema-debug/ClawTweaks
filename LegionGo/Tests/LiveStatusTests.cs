// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Threading.Tasks;
using LegionTweaks;

internal static partial class Program
{
    private static void LiveStatusTests()
    {
        Test("Read-only status refresh skips an occupied command channel without queuing a request", () => {
            var pending = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            var channel = new RequestChannel((request, budget) => {
                calls++; Assert(request.Operation == "controller_split_general" && budget == 60000, "read was sent behind the command");
                return pending.Task;
            });
            var command = channel.Send(new Request { Operation = "controller_split_general" });
            Assert(channel.TryReadStatus().GetAwaiter().GetResult() == null && calls == 1 && !command.IsCompleted, "poll waited for or raced a command");
            pending.SetResult(new Response { Success = true });
            Assert(command.GetAwaiter().GetResult().Success && calls == 1, "skipped poll was sent later");
        });
        Test("Live status uses a fixed immutable read-only request and short response budget", () => {
            var channel = new RequestChannel((request, budget) => {
                Assert(request.Operation == "recording_status" && request.Values == null && request.Profile == null
                    && request.Key == null && request.Display == null && request.RtssAppearance == null && !request.Ac
                    && budget == 5000, "status path admitted a write payload or long command timeout");
                return Task.FromResult(new Response { Success = true, Controller = new ControllerStatus { Active = true, Frames = 42, Input = new GamepadFrame { LT = 255 } } });
            });
            var response = channel.TryReadStatus().GetAwaiter().GetResult();
            Assert(response.Controller.Frames == 42 && response.Controller.Input.LT == 255, "status path lost live input");
        });
        Test("Status transport failure releases the channel for manual refresh and commands", () => {
            int calls = 0;
            var channel = new RequestChannel((request, budget) => {
                if (++calls == 1) throw new TimeoutException("status timed out");
                return Task.FromResult(new Response { Success = true });
            });
            MustFail(() => channel.TryReadStatus().GetAwaiter().GetResult());
            Assert(channel.TryReadStatus().GetAwaiter().GetResult().Success
                && channel.Send(new Request { Operation = "status" }).GetAwaiter().GetResult().Success && calls == 3, "failure retained the channel lock");
        });
        Test("Repeated helper status returns fresh input and release without sensor reads or writes", () => WithRecordingDirectory(folder => {
            var hardware = new Fake(); var journal = new Journal(); var controller = new LiveStatusController();
            using (var session = new HelperSession(new ControlEngine(hardware, journal, controller), folder, false, controller: controller)) {
                controller.Status = new ControllerStatus { Active = true, Connected = true, Frames = 5, Input = new GamepadFrame { LT = 255 } };
                var held = session.Execute(new Request { Operation = "recording_status" });
                controller.Status = new ControllerStatus { Active = true, Connected = true, Frames = 25 };
                var released = session.Execute(new Request { Operation = "recording_status" });
                Assert(held.Success && held.Controller.Input.LT == 255 && held.Controller.Frames == 5
                    && released.Success && released.Controller.Input.LT == 0 && released.Controller.Frames == 25, "helper cached a held input across refreshes");
                Assert(hardware.Reads.Count == 0 && hardware.Writes.Count == 0 && journal.Saves == 0 && controller.Applies == 0,
                    "live viewer changed controller, energy or sampled hardware");
            }
        }));
    }
    private sealed class LiveStatusController : IControllerService
    {
        internal ControllerStatus Status = new ControllerStatus();
        internal int Applies;
        public ControllerLayout Layout => new ControllerLayout();
        public ControllerStatus GetStatus() => Status;
        public void Preflight(ControllerLayout layout) { }
        public void Apply(ControllerLayout layout) { Applies++; }
        public void Dispose() { }
    }
}
