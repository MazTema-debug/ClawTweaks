// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LegionTweaks.Widget
{
    // Incomplete form values stay in the view; only validated shared DTOs cross IPC.
    internal sealed class RemapForm
    {
        internal RemapInput Input;
        internal RemapActionKind Kind;
        internal string Keys = "";
        internal RemapMouseButton MouseButton = RemapMouseButton.Left;
        internal int X = 1, Y, Button = 4096, Player = 1, Direction = 1;
        internal RemapAxis Axis = RemapAxis.LX;
        internal RemapFunction Function = RemapFunction.GameBar;
        internal RemapBehavior Behavior;
        internal int Threshold = 50, Amount = 100;

        internal RemapBehavior[] Behaviors => Kind == RemapActionKind.Original || Kind == RemapActionKind.None
            ? new[] { RemapBehavior.Hold }
            : Kind == RemapActionKind.Function || Kind == RemapActionKind.MouseWheel
                ? new[] { RemapBehavior.Tap }
                : RemapCatalog.IsRelative(Input) ? new[] { RemapBehavior.Hold, RemapBehavior.Tap }
                : new[] { RemapBehavior.Hold, RemapBehavior.Tap, RemapBehavior.Toggle };

        internal void Normalize()
        {
            if (!Behaviors.Contains(Behavior)) Behavior = Behaviors[0];
            if (Axis == RemapAxis.LT || Axis == RemapAxis.RT) Direction = 1;
            if (Kind == RemapActionKind.MouseMove && Math.Abs(X) + Math.Abs(Y) != 1) { X = 1; Y = 0; }
            if (Kind == RemapActionKind.MouseWheel && Y != -1 && Y != 1) Y = 1;
        }

        internal RemapBinding Build(RemapMode mode)
        {
            var action = new RemapAction { Kind = Kind };
            switch (Kind)
            {
                case RemapActionKind.Keyboard: action.Keys = RemapText.ParseKeys(Keys); break;
                case RemapActionKind.MouseButton: action.MouseButton = MouseButton; break;
                case RemapActionKind.MouseMove: action.X = X; action.Y = Y; break;
                case RemapActionKind.MouseWheel: action.Y = Y == -1 ? -1 : 1; break;
                case RemapActionKind.GamepadButton: action.Button = Button; action.Player = Player; break;
                case RemapActionKind.GamepadAxis: action.Axis = Axis; action.Direction = Direction; action.Player = Player; break;
                case RemapActionKind.Function: action.Function = Function; break;
            }
            var binding = new RemapBinding { Input = Input, Action = action, Behavior = Behavior, Threshold = Threshold, Amount = Amount };
            binding.Validate(mode);
            return binding;
        }

        internal static RemapForm From(RemapBinding binding)
        {
            var a = binding.Action;
            var result = new RemapForm { Input = binding.Input, Kind = a.Kind, Behavior = binding.Behavior,
                Threshold = binding.Threshold, Amount = binding.Amount };
            switch (a.Kind)
            {
                case RemapActionKind.Keyboard: result.Keys = RemapText.Keys(a.Keys); break;
                case RemapActionKind.MouseButton: result.MouseButton = a.MouseButton; break;
                case RemapActionKind.MouseMove: result.X = a.X; result.Y = a.Y; break;
                case RemapActionKind.MouseWheel: result.Y = a.Y; break;
                case RemapActionKind.GamepadButton: result.Button = a.Button; result.Player = a.Player; break;
                case RemapActionKind.GamepadAxis: result.Axis = a.Axis; result.Direction = a.Direction; result.Player = a.Player; break;
                case RemapActionKind.Function: result.Function = a.Function; break;
            }
            return result;
        }

        internal string Summary
        {
            get
            {
                switch (Kind)
                {
                    case RemapActionKind.Keyboard: return string.IsNullOrWhiteSpace(Keys) ? "Elige una combinación" : Keys;
                    case RemapActionKind.MouseButton: return "Ratón · " + RemapText.MouseButton(MouseButton);
                    case RemapActionKind.MouseMove: return "Puntero " + (X < 0 ? "←" : X > 0 ? "→" : Y < 0 ? "↑" : "↓");
                    case RemapActionKind.MouseWheel: return "Rueda " + (Y < 0 ? "↓" : "↑");
                    case RemapActionKind.GamepadButton: return "J" + Player + " · " + ControllerLayout.Labels[Array.IndexOf(ControllerLayout.Buttons, Button)];
                    case RemapActionKind.GamepadAxis: return "J" + Player + " · " + Axis + (Direction < 0 ? " −" : " +");
                    case RemapActionKind.Function: return RemapText.Function(Function);
                    default: return Kind == RemapActionKind.None ? "Sin acción" : "Original";
                }
            }
        }
    }

    internal sealed class RemapDraft
    {
        internal string Id, Name;
        internal RemapMode Mode;
        internal bool Dirty;
        internal int EditVersion;
        internal readonly Dictionary<RemapInput, RemapForm> Forms = new Dictionary<RemapInput, RemapForm>();
        internal void Touch() { Dirty = true; EditVersion++; }
        internal RemapForm Get(RemapInput input)
        {
            RemapForm form;
            if (!Forms.TryGetValue(input, out form)) Forms[input] = form = new RemapForm { Input = input };
            return form;
        }
        internal RemapProfile Build()
        {
            if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("Escribe un nombre para la distribución.");
            var bindings = new List<RemapBinding>();
            foreach (var form in Forms.Values.Where(f => f.Kind != RemapActionKind.Original).OrderBy(f => f.Input)) {
                try { bindings.Add(form.Build(Mode)); }
                catch (ArgumentException ex) { throw new ArgumentException(RemapText.Input(form.Input) + ": " + ex.Message, ex); }
            }
            var profile = new RemapProfile { Id = Id, Name = (Name ?? "").Trim(), Mode = Mode,
                Bindings = bindings.ToArray() };
            profile.Validate();
            return profile;
        }
        internal static RemapDraft From(RemapProfile profile)
        {
            var result = new RemapDraft { Id = profile.Id, Name = profile.Name, Mode = profile.Mode };
            foreach (var binding in profile.Bindings) result.Forms[binding.Input] = RemapForm.From(binding);
            return result;
        }
    }

    internal static class RemapText
    {
        internal static string Mode(RemapMode mode) => mode == RemapMode.Fps ? "FPS" : mode == RemapMode.Split ? "Dos jugadores" : "Mando completo";
        internal static string Behavior(RemapBehavior behavior) => behavior == RemapBehavior.Tap ? "Pulso breve" : behavior == RemapBehavior.Toggle ? "Alternar al pulsar" : "Mientras se mantiene pulsado";
        internal static string Function(RemapFunction function) => function == RemapFunction.GameBar ? "Abrir / cerrar Game Bar" : function == RemapFunction.PauseRemap ? "Pausar remapeo" : "Devolver controles originales";
        internal static string MouseButton(RemapMouseButton button) => new[] { "", "Clic izquierdo", "Clic derecho", "Clic central", "Atrás", "Adelante" }[(int)button];
        internal static string Kind(RemapActionKind kind) => new[] { "Original", "Sin acción", "Teclado / combinación", "Clic de ratón", "Mover puntero", "Rueda de ratón", "Botón de mando virtual", "Eje de mando virtual", "Función de LegionTweaks" }[(int)kind];
        internal static int Group(RemapInput input) => input <= RemapInput.Guide ? 0 : input <= RemapInput.M3 ? 1
            : input <= RemapInput.LeftRight ? 2 : input <= RemapInput.RightRight ? 3 : input <= RemapInput.WheelClick ? 4 : input <= RemapInput.SensorRight ? 5 : 6;
        internal static string Input(RemapInput input)
        {
            var labels = new[] { "A", "B", "X", "Y", "Cruceta ↑", "Cruceta ↓", "Cruceta ←", "Cruceta →", "LB", "RB / M1", "LT", "RT", "Clic stick izquierdo", "Clic stick derecho", "Vista", "Menú", "Guía",
                "Legion L", "Legion R", "Y1", "Y2", "Y3", "M2", "M3", "Stick izquierdo ↑", "Stick izquierdo ↓", "Stick izquierdo ←", "Stick izquierdo →", "Stick derecho ↑", "Stick derecho ↓", "Stick derecho ←", "Stick derecho →",
                "Rueda ↑", "Rueda ↓", "Clic de rueda", "Sensor FPS ↑", "Sensor FPS ↓", "Sensor FPS ←", "Sensor FPS →", "Panel táctil ↑", "Panel táctil ↓", "Panel táctil ←", "Panel táctil →" };
            return labels[(int)input];
        }

        private static readonly Dictionary<int, string> KeyNames = new Dictionary<int, string> {
            {8,"Retroceso"}, {9,"Tab"}, {12,"Borrar"}, {13,"Enter"}, {16,"Shift"}, {17,"Ctrl"}, {18,"Alt"}, {19,"Pausa"}, {20,"Bloq Mayús"},
            {27,"Escape"}, {32,"Espacio"}, {33,"Re Pág"}, {34,"Av Pág"}, {35,"Fin"}, {36,"Inicio"}, {37,"Izquierda"}, {38,"Arriba"}, {39,"Derecha"}, {40,"Abajo"},
            {44,"Impr Pant"}, {45,"Insert"}, {46,"Supr"}, {91,"Win izquierdo"}, {92,"Win derecho"}, {93,"Menú contextual"},
            {106,"Multiplicar numérico"}, {107,"Sumar numérico"}, {108,"Separador numérico"}, {109,"Restar numérico"}, {110,"Decimal numérico"}, {111,"Dividir numérico"},
            {144,"Bloq Num"}, {145,"Bloq Despl"}, {160,"Shift izquierdo"}, {161,"Shift derecho"}, {162,"Ctrl izquierdo"}, {163,"Ctrl derecho"}, {164,"Alt izquierdo"}, {165,"Alt derecho"},
            {166,"Navegador atrás"}, {167,"Navegador adelante"}, {168,"Navegador actualizar"}, {169,"Navegador detener"}, {170,"Navegador buscar"}, {171,"Navegador favoritos"}, {172,"Navegador inicio"},
            {173,"Silenciar"}, {174,"Bajar volumen"}, {175,"Subir volumen"}, {176,"Pista siguiente"}, {177,"Pista anterior"}, {178,"Detener reproducción"}, {179,"Reproducir / pausar"},
            {180,"Correo"}, {181,"Reproductor multimedia"}, {182,"Aplicación 1"}, {183,"Aplicación 2"},
            {186,"OEM punto y coma"}, {187,"OEM suma"}, {188,"OEM coma"}, {189,"OEM guion"}, {190,"OEM punto"}, {191,"OEM barra"}, {192,"OEM acento"},
            {219,"OEM corchete izquierdo"}, {220,"OEM barra inversa"}, {221,"OEM corchete derecho"}, {222,"OEM comillas"}, {223,"OEM 8"}, {226,"OEM 102"}
        };
        internal static string Key(int key)
        {
            string name;
            if (KeyNames.TryGetValue(key, out name)) return name;
            if (key >= 48 && key <= 57 || key >= 65 && key <= 90) return ((char)key).ToString();
            if (key >= 112 && key <= 135) return "F" + (key - 111);
            if (key >= 96 && key <= 105) return "Num " + (key - 96);
            return "0x" + key.ToString("X2", CultureInfo.InvariantCulture);
        }
        internal static string Keys(int[] keys) => string.Join(" + ", (keys ?? new int[0]).Select(Key));
        internal static int[] ParseKeys(string text)
        {
            var known = Enumerable.Range(0, 256).Where(RemapCatalog.IsKeyboardKey).ToDictionary(Key, k => k, StringComparer.OrdinalIgnoreCase);
            known["Esc"] = 27; known["Control"] = 17; known["Win"] = 91; known["Suprimir"] = 46;
            var parts = (text ?? "").Split('+');
            var keys = new List<int>();
            foreach (string part in parts)
            {
                string token = part.Trim(); int key;
                if (!known.TryGetValue(token, out key))
                    throw new ArgumentException("Elige las teclas del selector o escribe una combinación como Ctrl + F14.");
                keys.Add(key);
            }
            // Shared validation rejects reserved keys, duplicate modifiers and >6 keys.
            new RemapAction { Kind = RemapActionKind.Keyboard, Keys = keys.ToArray() }.Validate(RemapMode.Gamepad);
            return keys.ToArray();
        }
    }
}
