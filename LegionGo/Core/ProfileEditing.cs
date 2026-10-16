// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;

namespace LegionTweaks
{
    // Pure draft operations. Neither operation modifies the saved catalog or
    // applies any setting; the catalog's normal validation still gates Save.
    public static class ProfileEditing
    {
        public static ProfileDefinition DuplicateSaved(ProfileCatalog catalog, string savedId)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            catalog.Validate();
            var saved = catalog.Profiles.FirstOrDefault(x => string.Equals(x.Id, savedId, StringComparison.OrdinalIgnoreCase));
            if (saved == null) throw new ArgumentException("Selecciona un perfil guardado para duplicarlo.");
            var copy = saved.Copy();
            do { copy.Id = Guid.NewGuid().ToString("D"); }
            while (catalog.Profiles.Any(x => string.Equals(x.Id, copy.Id, StringComparison.OrdinalIgnoreCase)));
            string original = saved.Name.Trim();
            for (int number = 1; ; number++)
            {
                string suffix = number == 1 ? " (copia)" : " (copia " + number + ")";
                int length = Math.Min(original.Length, 40 - suffix.Length);
                if (length < original.Length && length > 0 && char.IsHighSurrogate(original[length - 1])) length--;
                string name = original.Substring(0, length).TrimEnd() + suffix;
                if (catalog.Profiles.Any(x => string.Equals(x.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))) continue;
                copy.Name = name; copy.Validate(); return copy;
            }
        }

        public static ProfileDefinition ResetController(ProfileDefinition draft)
        {
            if (draft == null) throw new ArgumentNullException(nameof(draft));
            var copy = draft.Copy();
            // Null means Keep. Disabled means Physical. Both choices are retained;
            // defaults are merely staged for the next explicit save/application.
            if (draft.Controller != null) copy.Controller = new ControllerLayout { Enabled = draft.Controller.Enabled };
            return copy;
        }
    }
}
