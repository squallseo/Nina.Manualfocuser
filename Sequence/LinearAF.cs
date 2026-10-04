#region "copyright"

/*
    Copyright © 2025 Christian Palm (christian@palm-family.de)
    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Validations;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Rtg.ManualFocuser.SequenceItems {
    [ExportMetadata("Name", "Linear AF")]
    [ExportMetadata("Description", "Run Linear Auto Focus")]
    [ExportMetadata("Icon", "CameraSVG")]
    [ExportMetadata("Category", "Lens")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class LinearAF : SequenceItem, IValidatable
    {
        private IList<string> issues = new List<string>();

        public IList<string> Issues
        {
            get => issues;
            set
            {
                issues = value;
                RaisePropertyChanged();
            }
        }

        private readonly IFocuserMediator focuserMediator;

        [ImportingConstructor]
        public LinearAF(IFocuserMediator focuserMediator)
        {
            this.focuserMediator = focuserMediator;
        }

        public override object Clone()
        {
            return new LinearAF(focuserMediator)
            {
                Icon = Icon,
                Name = Name,
                Category = Category,
                Description = Description,
            };
        }

        public bool Validate()
        {
            List<string> i = new List<string>();
            if (!focuserMediator.GetInfo().Connected)
            {
                i.Add("Camera is not connected");
            }
            Issues = i;
            return i.Count == 0;
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
        {
            // First try to find and call RequestLinearAFAsync implemented in the RTG.ManualFocuser assembly.
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try
                    {
                        types = asm.GetTypes();
                    }
                    catch (ReflectionTypeLoadException rtl)
                    {
                        types = rtl.Types;
                    }

                    if (types == null)
                    {
                        continue;
                    }

                    foreach (var t in types)
                    {
                        if (t == null)
                        {
                            continue;
                        }

                        // look for types in the RTG.ManualFocuser namespace (case-insensitive) that expose RequestLinearAFAsync
                        if (!t.FullName?.StartsWith("RTG.ManualFocuser", StringComparison.OrdinalIgnoreCase) == false)
                        {
                            continue;
                        }

                        MethodInfo reqMethod = t.GetMethod("RequestLinearAFAsync", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        if (reqMethod == null)
                        {
                            continue;
                        }

                        Logger.Info("Invoking RequestLinearAFAsync on type {0}", t.FullName);

                        object targetInstance = null;

                        object result = reqMethod.Invoke(targetInstance, null);

                        if (result is Task task)
                        {
                            await task.ConfigureAwait(false);
                            return;
                        }

                        // synchronous result (bool or void)
                        return;
                    }
                }
            }
            catch (TargetInvocationException tie)
            {
                Logger.Error(tie, "Error invoking RequestLinearAFAsync");
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Unable to locate RTG.ManualFocuser.RequestLinearAFAsync via reflection, falling back to StartCalibration ICommand.");
            }
        }
    }
}
