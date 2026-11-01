// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LegionTweaks;
using LegionTweaks.Widget;

internal static partial class Program
{
    private sealed class EditorService : IRemapEditorService
    {
        internal readonly RemapMemoryStorage Storage = new RemapMemoryStorage();
        internal readonly List<Request> Requests = new List<Request>();
        internal Func<Request, Task<Response>> Override;
        private readonly RemapEditingService backend;
        internal EditorService(params RemapProfile[] profiles)
        {
            Storage.Bytes = new RemapProfiles { Profiles = profiles }.Encode();
            backend = new RemapEditingService(Storage, true);
        }
        internal Response Execute(Request request)
        {
            Assert(request.Profile == null && request.Display == null && request.Key == null && request.Values == null && !request.Ac && request.GameBarInput == null,
                "editor request mixed in power, display or Game Bar state");
            Assert(request.Operation == "remap_status" || request.Operation == "remap_save" || request.Operation == "remap_delete", "editor operated a native route");
            try { return new Response { Success = true, SupportsRemapEditing = true, Remap = backend.Execute(request.Operation, request.Remap) }; }
            catch (Exception ex) { return new Response { SupportsRemapEditing = true, Message = ex.Message }; }
        }
        public Task<Response> Send(Request request)
        {
            Requests.Add(request);
            return Override == null ? Task.FromResult(Execute(request)) : Override(request);
        }
    }
    private static RemapEditor ReadyEditor(EditorService service)
    {
        var editor = new RemapEditor(service);
        Assert(editor.Refresh().GetAwaiter().GetResult(), "catalog failed to load");
        return editor;
    }
    private static void EditorKeys(RemapEditor editor, string text)
    {
        var form = editor.Current.Get(RemapInput.Y3);
        form.Kind = RemapActionKind.Keyboard; form.Keys = text; editor.Current.Touch();
    }
    private static void RemapEditorTests()
    {
        Test("Editor switching modes and drafts retains incomplete text without IPC", () => {
            var service = new EditorService(); var editor = new RemapEditor(service);
            editor.SelectMode(RemapMode.Fps); var first = editor.Current;
            EditorKeys(editor, "Ctrl + "); first.Name = ""; first.Touch();
            editor.SelectMode(RemapMode.Split); EditorKeys(editor, "F24");
            editor.SelectMode(RemapMode.Fps);
            Assert(ReferenceEquals(first, editor.Current) && editor.Current.Name == "" && editor.Current.Get(RemapInput.Y3).Keys == "Ctrl + ", "incomplete draft lost");
            editor.New(RemapMode.Fps); editor.Select(first.Id);
            Assert(ReferenceEquals(first, editor.Current) && service.Requests.Count == 0 && service.Storage.Writes == 0, "select or new performed I/O");
        });
        Test("Editor with old helper preserves draft and cannot save", () => {
            var service = new EditorService { Override = request => Task.FromResult(new Response { Success = true }) };
            var editor = new RemapEditor(service); EditorKeys(editor, "Ctrl + F14");
            Assert(!editor.Refresh().GetAwaiter().GetResult() && !editor.CanSave, "old helper enabled saving");
            Assert(!editor.Save().GetAwaiter().GetResult() && service.Requests.Count == 1 && editor.Current.Get(RemapInput.Y3).Keys == "Ctrl + F14", "old helper lost draft or sent mutation");
        });
        Test("Editor saves a validated snapshot through the real catalog service without activation", () => {
            var service = new EditorService(); var editor = ReadyEditor(service);
            editor.SelectMode(RemapMode.Fps); EditorKeys(editor, "Ctrl + Shift + F14");
            Assert(editor.Save().GetAwaiter().GetResult() && !editor.Current.Dirty && service.Storage.Writes == 1, "save did not confirm snapshot");
            var saved = RemapProfiles.Decode(service.Storage.Bytes).Profiles.Single();
            Assert(saved.Bindings.Single().Action.Keys.SequenceEqual(new[] { 17, 16, 125 }), "wrong chord persisted");
            EditorKeys(editor, "Escape");
            Assert(saved.Bindings.Single().Action.Keys[2] == 125 && editor.SavedCurrent.Bindings.Single().Action.Keys[2] == 125, "editing mutated saved snapshot");
            Assert(service.Requests.All(r => r.Operation != "remap_activate"), "save activated route");
        });
        Test("Editor detects two-view conflict and reload preserves the losing draft", () => {
            var profile = new RemapProfile { Name = "Compartida", Mode = RemapMode.Fps };
            var service = new EditorService(profile); var a = ReadyEditor(service); var b = ReadyEditor(service);
            a.Select(profile.Id); b.Select(profile.Id);
            EditorKeys(a, "F14"); Assert(a.Save().GetAwaiter().GetResult(), "first editor save failed");
            EditorKeys(b, "Escape"); Assert(!b.Save().GetAwaiter().GetResult() && b.ReloadRequired && !b.CanSave, "stale editor overwrote catalog");
            int calls = service.Requests.Count;
            Assert(!b.Save().GetAwaiter().GetResult() && service.Requests.Count == calls, "conflict silently retried");
            Assert(b.Refresh().GetAwaiter().GetResult() && b.Current.Get(RemapInput.Y3).Keys == "Escape" && b.Current.Dirty, "reload discarded local changes");
            Assert(b.Message.Contains("otro editor") && service.Storage.Writes == 1, "external changes were not explained");
            Assert(b.Save().GetAwaiter().GetResult() && service.Storage.Writes == 2, "explicit save after reload failed");
        });
        Test("Editor storage failure requires a fresh catalog and retains the full draft", () => {
            var service = new EditorService(); var editor = ReadyEditor(service); EditorKeys(editor, "Win + F24");
            service.Storage.FailWrite = true;
            Assert(!editor.Save().GetAwaiter().GetResult() && editor.Current.Dirty && editor.ReloadRequired, "write failure cleared changes");
            Assert(editor.Current.Get(RemapInput.Y3).Keys == "Win + F24" && service.Storage.Writes == 0, "failed storage changed data");
        });
        Test("Editor invalid combination and name never reach IPC", () => {
            var service = new EditorService(); var editor = ReadyEditor(service); int calls = service.Requests.Count;
            EditorKeys(editor, "Ctrl + ");
            Assert(!editor.Save().GetAwaiter().GetResult() && editor.CanSave, "incomplete form invalidated server catalog");
            EditorKeys(editor, "A"); editor.Current.Name = " "; editor.Current.Touch();
            Assert(!editor.Save().GetAwaiter().GetResult() && service.Requests.Count == calls, "invalid form crossed IPC");
        });
        Test("Editor profile selection and catalog refresh preserve separate dirty drafts", () => {
            var first = new RemapProfile { Name = "Uno" }; var second = new RemapProfile { Name = "Dos" };
            var service = new EditorService(first, second); var editor = ReadyEditor(service);
            editor.Select(first.Id); EditorKeys(editor, "Ctrl + ");
            editor.Select(second.Id); EditorKeys(editor, "Shift + A");
            editor.Refresh().GetAwaiter().GetResult(); editor.Select(first.Id);
            Assert(editor.Current.Get(RemapInput.Y3).Keys == "Ctrl + ", "first dirty draft was overwritten");
            editor.Select(second.Id); Assert(editor.Current.Get(RemapInput.Y3).Keys == "Shift + A", "second dirty draft was overwritten");
            int calls = service.Requests.Count; editor.RestoreSaved();
            Assert(!editor.Current.Dirty && editor.Current.Forms.Count == 0 && calls == service.Requests.Count, "restore sent I/O or kept unwanted assignment");
        });
        Test("Editor closed-view response cannot replace a new query or release its busy state", () => {
            var oldReply = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
            var newReply = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new EditorService(); int calls = 0;
            service.Override = request => ++calls == 1 ? oldReply.Task : newReply.Task;
            var editor = new RemapEditor(service); EditorKeys(editor, "F14");
            var old = editor.Refresh(); editor.Invalidate(); var current = editor.Refresh();
            oldReply.SetResult(service.Execute(new Request { Operation = "remap_status" }));
            Assert(!old.GetAwaiter().GetResult() && editor.IsBusy && editor.Revision == null, "stale view response changed new query");
            newReply.SetResult(service.Execute(new Request { Operation = "remap_status" }));
            Assert(current.GetAwaiter().GetResult() && !editor.IsBusy && editor.Current.Get(RemapInput.Y3).Keys == "F14", "reopened view lost its draft");
        });
        Test("Editor save reply cannot mark later edits as saved", () => {
            var service = new EditorService(); var editor = ReadyEditor(service); EditorKeys(editor, "F14");
            var reply = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
            Response committed = null;
            service.Override = request => { committed = service.Execute(request); return reply.Task; };
            var saving = editor.Save(); EditorKeys(editor, "Escape"); reply.SetResult(committed);
            Assert(saving.GetAwaiter().GetResult() && editor.Current.Dirty && editor.Current.Get(RemapInput.Y3).Keys == "Escape", "reply overwrote later edit");
            Assert(editor.SavedCurrent.Bindings.Single().Action.Keys.Single() == 125, "saved copy was confused with current draft");
        });
        Test("Editor rejects success without readback of the saved distribution", () => {
            var service = new EditorService(); var editor = ReadyEditor(service); EditorKeys(editor, "A");
            service.Override = request => Task.FromResult(service.Execute(new Request { Operation = "remap_status" }));
            Assert(!editor.Save().GetAwaiter().GetResult() && editor.Current.Dirty && editor.ReloadRequired, "unconfirmed save reported success");
        });
        Test("Editor deleting a saved copy preserves a dirty local draft", () => {
            var profile = new RemapProfile { Name = "Eliminar", Mode = RemapMode.Fps };
            var service = new EditorService(profile); var editor = ReadyEditor(service); editor.Select(profile.Id); EditorKeys(editor, "Ctrl + F14");
            Assert(editor.Delete(profile.Id).GetAwaiter().GetResult() && editor.Catalog.Profiles.Length == 0, "saved copy remained");
            Assert(editor.Current.Id == profile.Id && editor.Current.Dirty && editor.Current.Get(RemapInput.Y3).Keys == "Ctrl + F14", "delete destroyed dirty local draft");
            Assert(editor.Save().GetAwaiter().GetResult() && editor.Catalog.Profiles.Length == 1, "retained draft could not be explicitly saved again");
        });
        Test("Editor does not keep an externally deleted clean profile as saved", () => {
            var profile = new RemapProfile { Name = "Borrada en otra vista" };
            var service = new EditorService(profile); var a = ReadyEditor(service); var b = ReadyEditor(service);
            a.Select(profile.Id); b.Select(profile.Id);
            Assert(a.Delete(profile.Id).GetAwaiter().GetResult(), "external delete failed");
            Assert(b.Refresh().GetAwaiter().GetResult() && b.Current.Id != profile.Id && b.SavedCurrent == null, "clean deleted profile remained selected");
            Assert(b.Choices.All(d => d.Id != profile.Id) && b.Current.Dirty, "deleted saved entry remained in choices");
        });
        Test("Editor keyboard chooser round trips OEM media function and modifier identities", () => {
            foreach (int key in Enumerable.Range(0, 256).Where(RemapCatalog.IsKeyboardKey))
                Assert(RemapText.ParseKeys(RemapText.Key(key)).Single() == key, "keyboard choice changed identity " + key);
            Assert(RemapText.ParseKeys("Ctrl + Shift + F24").SequenceEqual(new[] { 17, 16, 135 }), "named chord changed keys");
            RemapReject(() => RemapText.ParseKeys("Ctrl + Ctrl izquierdo"));
            RemapReject(() => RemapText.ParseKeys("A + B + C + D + E + F + G"));
        });
        Test("Editor form emits fields only for the selected destination", () => {
            var form = new RemapForm { Input = RemapInput.Y3, Kind = RemapActionKind.Keyboard, Keys = "Alt + F14", Player = 2, X = -1, Function = RemapFunction.PauseRemap };
            var keyboard = form.Build(RemapMode.Fps).Action;
            Assert(keyboard.Keys.Length == 2 && keyboard.Player == 0 && keyboard.X == 0 && keyboard.Function == RemapFunction.None, "hidden fields leaked into keyboard DTO");
            form.Kind = RemapActionKind.MouseMove; form.Normalize(); var mouse = form.Build(RemapMode.Fps).Action;
            Assert(mouse.Keys == null && mouse.X == -1 && mouse.Player == 0, "previous destination leaked into mouse DTO");
            form.Kind = RemapActionKind.Original; form.Normalize();
            Assert(form.Build(RemapMode.Fps).Action.Keys == null, "Original retained injected keys");
        });
        Test("Editor context enforces relative actions function pulses and positive triggers", () => {
            var form = new RemapForm { Input = RemapInput.WheelUp, Kind = RemapActionKind.MouseMove, Behavior = RemapBehavior.Toggle };
            form.Normalize(); Assert(form.Behavior == RemapBehavior.Hold && !form.Behaviors.Contains(RemapBehavior.Toggle), "relative toggle offered");
            form.Kind = RemapActionKind.Function; form.Normalize(); Assert(form.Build(RemapMode.Fps).Behavior == RemapBehavior.Tap, "function did not require pulse");
            form.Kind = RemapActionKind.GamepadAxis; form.Axis = RemapAxis.RT; form.Direction = -1; form.Normalize();
            Assert(form.Build(RemapMode.Fps).Action.Direction == 1, "negative trigger accepted");
        });
        Test("Editor J2 and sensor lanes respect the selected editing mode", () => {
            var form = new RemapForm { Input = RemapInput.A, Kind = RemapActionKind.GamepadButton, Player = 2 };
            Assert(form.Build(RemapMode.Split).Action.Player == 2, "J2 missing in split");
            RemapReject(() => form.Build(RemapMode.Fps));
            form.Input = RemapInput.SensorLeft; form.Player = 1;
            Assert(form.Build(RemapMode.Fps).Input == RemapInput.SensorLeft, "FPS sensor missing");
            RemapReject(() => form.Build(RemapMode.Gamepad));
        });
    }
}
