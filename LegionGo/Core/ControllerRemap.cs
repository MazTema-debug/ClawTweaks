// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;

namespace LegionTweaks
{
    public enum RemapMode { Gamepad, Fps, Split }
    // Directional lanes let each direction receive a different action. Values
    // are normalized by an identified input adapter, never inferred from a PID.
    public enum RemapInput
    {
        A, B, X, Y, DpadUp, DpadDown, DpadLeft, DpadRight,
        LB, RB, LT, RT, LeftClick, RightClick, View, Menu, Guide,
        LegionLeft, LegionRight, Y1, Y2, Y3, M2, M3,
        LeftUp, LeftDown, LeftLeft, LeftRight,
        RightUp, RightDown, RightLeft, RightRight,
        WheelUp, WheelDown, WheelClick,
        SensorUp, SensorDown, SensorLeft, SensorRight,
        TouchUp, TouchDown, TouchLeft, TouchRight
    }
    public enum RemapActionKind { Original, None, Keyboard, MouseButton, MouseMove, MouseWheel, GamepadButton, GamepadAxis, Function }
    public enum RemapBehavior { Hold, Tap, Toggle }
    public enum RemapMouseButton { None, Left, Right, Middle, Back, Forward }
    public enum RemapAxis { None, LX, LY, RX, RY, LT, RT }
    public enum RemapFunction { None, GameBar, PauseRemap, ReturnOriginal }

    public static class RemapCatalog
    {
        public static RemapInput[] Inputs => (RemapInput[])Enum.GetValues(typeof(RemapInput));
        public static bool IsRelative(RemapInput input) => input >= RemapInput.WheelUp && input != RemapInput.WheelClick;
        public static bool IsAnalog(RemapInput input) => input == RemapInput.LT || input == RemapInput.RT
            || (input >= RemapInput.LeftUp && input <= RemapInput.RightRight);
        public static bool IsModifier(int key) => (key >= 0x10 && key <= 0x12) || key == 0x5b || key == 0x5c || (key >= 0xa0 && key <= 0xa5);
        // Generic modifiers select the left identity. They must share ownership
        // with an explicit left modifier rather than cause two independent ups.
        public static int CanonicalKey(int key) => key == 0x10 ? 0xa0 : key == 0x11 ? 0xa2 : key == 0x12 ? 0xa4 : key;
        // Windows VK identities, including F1-F24, OEM and multimedia keys.
        // Mouse VKs, reserved values and VK_GAMEPAD_* are separate destinations.
        public static bool IsKeyboardKey(int key) => key == 0x08 || key == 0x09 || key == 0x0c || key == 0x0d
            || (key >= 0x10 && key <= 0x14) || (key >= 0x15 && key <= 0x2f)
            || (key >= 0x30 && key <= 0x39) || (key >= 0x41 && key <= 0x5d) || key == 0x5f
            || (key >= 0x60 && key <= 0x87) || key == 0x90 || key == 0x91
            || (key >= 0xa0 && key <= 0xb7) || (key >= 0xba && key <= 0xc0)
            || (key >= 0xdb && key <= 0xdf) || key == 0xe2;
        internal static void RequireEnum<T>(T value) where T : struct
        { if (!Enum.IsDefined(typeof(T), value)) throw new ArgumentException("Valor de remapeo desconocido."); }
    }

    [DataContract]
    public sealed class RemapAction
    {
        [DataMember(IsRequired = true)] public RemapActionKind Kind { get; set; }
        [DataMember(EmitDefaultValue = false)] public int[] Keys { get; set; }
        [DataMember(EmitDefaultValue = false)] public RemapMouseButton MouseButton { get; set; }
        // MouseMove: cardinal direction. MouseWheel: Y = -1/+1, X = 0.
        [DataMember(EmitDefaultValue = false)] public int X { get; set; }
        [DataMember(EmitDefaultValue = false)] public int Y { get; set; }
        [DataMember(EmitDefaultValue = false)] public int Button { get; set; }
        [DataMember(EmitDefaultValue = false)] public RemapAxis Axis { get; set; }
        [DataMember(EmitDefaultValue = false)] public int Direction { get; set; }
        [DataMember(EmitDefaultValue = false)] public int Player { get; set; }
        [DataMember(EmitDefaultValue = false)] public RemapFunction Function { get; set; }

        public void Validate(RemapMode mode)
        {
            RemapCatalog.RequireEnum(Kind); RemapCatalog.RequireEnum(MouseButton);
            RemapCatalog.RequireEnum(Axis); RemapCatalog.RequireEnum(Function);
            if ((Kind != RemapActionKind.Keyboard && Keys != null) || (Kind != RemapActionKind.MouseButton && MouseButton != 0)
                || (Kind != RemapActionKind.MouseMove && Kind != RemapActionKind.MouseWheel && (X != 0 || Y != 0))
                || (Kind != RemapActionKind.GamepadButton && Button != 0)
                || (Kind != RemapActionKind.GamepadAxis && (Axis != 0 || Direction != 0))
                || (Kind != RemapActionKind.GamepadAxis && Kind != RemapActionKind.GamepadButton && Player != 0)
                || (Kind != RemapActionKind.Function && Function != 0))
                throw new ArgumentException("La acción contiene campos de otro destino.");
            if (Kind == RemapActionKind.Keyboard && (Keys == null || Keys.Length == 0 || Keys.Length > 6
                || Keys.Select(RemapCatalog.CanonicalKey).Distinct().Count() != Keys.Length || Keys.Any(k => !RemapCatalog.IsKeyboardKey(k))))
                throw new ArgumentException("Selecciona de una a seis teclas válidas sin repetir.");
            if (Kind == RemapActionKind.MouseButton && MouseButton == RemapMouseButton.None)
                throw new ArgumentException("Selecciona un botón de ratón.");
            if (Kind == RemapActionKind.MouseMove && Math.Abs((long)X) + Math.Abs((long)Y) != 1
                || Kind == RemapActionKind.MouseWheel && (X != 0 || (Y != -1 && Y != 1)))
                throw new ArgumentException("Selecciona una dirección válida.");
            if (Kind == RemapActionKind.GamepadButton && !ControllerLayout.Buttons.Contains(Button))
                throw new ArgumentException("Selecciona un botón de mando válido.");
            if (Kind == RemapActionKind.GamepadAxis && (Axis == RemapAxis.None || (Direction != -1 && Direction != 1)
                || ((Axis == RemapAxis.LT || Axis == RemapAxis.RT) && Direction != 1)))
                throw new ArgumentException("Selecciona un eje y una dirección válidos.");
            if ((Kind == RemapActionKind.GamepadButton || Kind == RemapActionKind.GamepadAxis)
                && (Player < 1 || Player > (mode == RemapMode.Split ? 2 : 1)))
                throw new ArgumentException("Jugador de destino incompatible con el modo.");
            if (Kind == RemapActionKind.Function && Function == RemapFunction.None)
                throw new ArgumentException("Selecciona una función disponible.");
        }
    }

    [DataContract]
    public sealed class RemapBinding
    {
        [DataMember(IsRequired = true)] public RemapInput Input { get; set; }
        [DataMember(IsRequired = true)] public RemapAction Action { get; set; } = new RemapAction();
        [DataMember(IsRequired = true)] public RemapBehavior Behavior { get; set; }
        [DataMember(IsRequired = true)] public int Threshold { get; set; } = 50;
        [DataMember(IsRequired = true)] public int Amount { get; set; } = 100;
        public void Validate(RemapMode mode)
        {
            RemapCatalog.RequireEnum(Input); RemapCatalog.RequireEnum(Behavior);
            if (Action == null || Threshold < 1 || Threshold > 100 || Amount < 1 || Amount > 100)
                throw new ArgumentException("Asignación incompleta o fuera de rango.");
            Action.Validate(mode);
            if ((Action.Kind == RemapActionKind.Original || Action.Kind == RemapActionKind.None) && Behavior != RemapBehavior.Hold
                || (Action.Kind == RemapActionKind.Function || Action.Kind == RemapActionKind.MouseWheel) && Behavior != RemapBehavior.Tap
                || RemapCatalog.IsRelative(Input) && Behavior == RemapBehavior.Toggle)
                throw new ArgumentException("Comportamiento incompatible con la acción o la entrada relativa.");
            if (Input >= RemapInput.SensorUp && Input <= RemapInput.SensorRight && mode != RemapMode.Fps)
                throw new ArgumentException("El sensor FPS pertenece al modo FPS.");
        }
    }

    [DataContract]
    public sealed class RemapProfile
    {
        [DataMember(IsRequired = true)] public int Version { get; set; } = 1;
        [DataMember(IsRequired = true)] public string Id { get; set; } = Guid.NewGuid().ToString("D");
        [DataMember(IsRequired = true)] public string Name { get; set; } = "Mi distribución";
        [DataMember(IsRequired = true)] public RemapMode Mode { get; set; }
        [DataMember(IsRequired = true)] public RemapBinding[] Bindings { get; set; } = new RemapBinding[0];
        public void Validate()
        {
            Guid id; RemapCatalog.RequireEnum(Mode);
            if (Version != 1 || !Guid.TryParseExact(Id, "D", out id) || id == Guid.Empty
                || string.IsNullOrWhiteSpace(Name) || Name.Length > 40 || Name != Name.Trim() || Name.Any(char.IsControl)
                || Bindings == null || Bindings.Length > RemapCatalog.Inputs.Length || Bindings.Any(b => b == null)
                || Bindings.Select(b => b.Input).Distinct().Count() != Bindings.Length)
                throw new ArgumentException("Distribución de remapeo inválida o incompatible.");
            foreach (var binding in Bindings) binding.Validate(Mode);
        }
        public RemapProfile Copy() { Validate(); return Protocol.Decode<RemapProfile>(Protocol.Encode(this)); }
    }

    // No Enabled/Active field: importing or loading cannot activate a route.
    [DataContract]
    public sealed class RemapProfiles
    {
        public const int MaximumBytes = 48000, MaximumProfiles = 16;
        [DataMember(IsRequired = true)] public int Version { get; set; } = 1;
        [DataMember(IsRequired = true)] public RemapProfile[] Profiles { get; set; } = new RemapProfile[0];
        public void Validate()
        {
            if (Version != 1 || Profiles == null || Profiles.Length > MaximumProfiles || Profiles.Any(p => p == null))
                throw new ArgumentException("Catálogo de remapeo inválido o incompatible.");
            foreach (var profile in Profiles) profile.Validate();
            if (Profiles.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Profiles.Length
                || Profiles.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Profiles.Length)
                throw new ArgumentException("Los identificadores y nombres deben ser únicos.");
        }
        public byte[] Encode()
        {
            Validate(); var bytes = Encoding.UTF8.GetBytes(Protocol.Encode(this));
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("Catálogo de remapeo demasiado grande.");
            return bytes;
        }
        public static RemapProfiles Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumBytes)
                throw new InvalidDataException("Archivo de remapeo vacío o demasiado grande.");
            try
            {
                string json = new UTF8Encoding(false, true).GetString(bytes);
                if (json.Length > 0 && json[0] == '\ufeff') json = json.Substring(1);
                var result = Protocol.Decode<RemapProfiles>(json);
                if (result == null) throw new ArgumentException("Catálogo vacío.");
                result.Validate(); return result;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is SerializationException)
            { throw new InvalidDataException("Archivo de remapeo inválido.", ex); }
        }
    }

    // Only an identified, verified native adapter may supply these capabilities.
    // UI choices and HID presence are never evidence of native substitution.
    public sealed class RemapCapabilities
    {
        public RemapMode Mode { get; set; }
        public RemapInput[] Inputs { get; set; } = new RemapInput[0];
        public RemapInput[] ReplacedInputs { get; set; } = new RemapInput[0];
        public bool Keyboard { get; set; }
        public bool Mouse { get; set; }
        public int VirtualPlayers { get; set; }
        public RemapFunction[] Functions { get; set; } = new RemapFunction[0];
        public void Require(RemapProfile profile)
        {
            profile.Validate(); RemapCatalog.RequireEnum(Mode);
            if (Inputs == null || ReplacedInputs == null || Functions == null || Mode != profile.Mode || VirtualPlayers < 0 || VirtualPlayers > 2)
                throw new InvalidOperationException("Ruta de remapeo incompatible.");
            foreach (var input in Inputs.Concat(ReplacedInputs)) RemapCatalog.RequireEnum(input);
            foreach (var function in Functions) RemapCatalog.RequireEnum(function);
            foreach (var binding in profile.Bindings.Where(b => b.Action.Kind != RemapActionKind.Original))
            {
                var a = binding.Action;
                if (!Inputs.Contains(binding.Input) || !ReplacedInputs.Contains(binding.Input))
                    throw new InvalidOperationException("No se ha verificado la lectura y sustitución de " + binding.Input + ".");
                if (a.Kind == RemapActionKind.Keyboard && !Keyboard
                    || (a.Kind == RemapActionKind.MouseButton || a.Kind == RemapActionKind.MouseMove || a.Kind == RemapActionKind.MouseWheel) && !Mouse
                    || (a.Kind == RemapActionKind.GamepadButton || a.Kind == RemapActionKind.GamepadAxis) && VirtualPlayers < a.Player
                    || a.Kind == RemapActionKind.Function && !Functions.Contains(a.Function))
                    throw new InvalidOperationException("El destino elegido no está disponible en esta ruta.");
            }
        }
    }

    [DataContract]
    public sealed class RemapEditingRequest
    {
        [DataMember(EmitDefaultValue = false)] public string Revision { get; set; }
        [DataMember(EmitDefaultValue = false)] public string Id { get; set; }
        [DataMember(EmitDefaultValue = false)] public RemapProfile Profile { get; set; }
    }
    [DataContract]
    public sealed class RemapEditingStatus
    {
        [DataMember] public bool EditingAvailable { get; set; }
        [DataMember] public bool ActivationAvailable { get; set; }
        [DataMember] public string Revision { get; set; }
        [DataMember] public RemapProfiles Catalog { get; set; }
        [DataMember] public string Message { get; set; }
    }
}
