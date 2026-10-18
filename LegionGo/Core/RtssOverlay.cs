// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Runtime.Serialization;

namespace LegionTweaks
{
    [DataContract]
    public sealed class RtssOverlayStatus
    {
        [DataMember] public bool Supports { get; set; } = true;
        [DataMember] public bool Enabled { get; set; }
        [DataMember] public bool Publishing { get; set; }
        [DataMember] public bool CleanupPending { get; set; }
        [DataMember] public string State { get; set; }
        [DataMember] public string Message { get; set; }
        [DataMember] public string Error { get; set; }
        [DataMember] public string LastSampleAtUtc { get; set; }
        [DataMember] public string LastPublishedAtUtc { get; set; }
        [DataMember] public long Updates { get; set; }
    }

    public sealed class RtssPublishResult
    {
        public string State { get; set; }
        public bool Published { get; set; }
        public bool CleanupPending { get; set; }
        public string Error { get; set; }
    }

    public interface IRtssOsdOutput : IDisposable
    {
        RtssPublishResult Publish(string text);
        RtssPublishResult Release();
    }

    public interface IRtssOverlay : IDisposable
    {
        void SetEnabled(bool enabled);
        RtssOverlayStatus GetStatus();
        void Observe(Response response);
        void Suspend();
    }
}
