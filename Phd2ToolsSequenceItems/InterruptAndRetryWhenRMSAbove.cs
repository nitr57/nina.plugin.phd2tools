using CommunityToolkit.Mvvm.ComponentModel;
using Newtonsoft.Json;
using NINA.Core.Interfaces;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Utility;
using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace nina.plugin.phd2tools.Phd2ToolsSequenceItems {

    [ExportMetadata("Name", "Interrupt and retry when RMS above")]
    [ExportMetadata("Description", "This trigger will interrupt an exposure when a guide pulse exceeds a certain value, wait until the guiding has recovered and then repeat the very same exposure instead of continuing with the next instruction. The other triggers of the container run for the repeated exposure as well, so a due meridian flip or autofocus still happens - but a \"dither after n exposures\" trigger counts the repeated frame too. In the rare case that the guiding exceeds the threshold in the very moment the exposure finishes, the frame can be exposed twice.")]
    [ExportMetadata("Icon", "PhdTools_Eye")]
    [ExportMetadata("Category", "Phd2 Tools")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public partial class InterruptAndRetryWhenRMSAbove : SequenceTrigger {
        private IGuiderMediator guiderMediator;

        /// <summary>Guide steps older than this mean the guider is not actually guiding</summary>
        private static readonly TimeSpan GuideStepTimeout = TimeSpan.FromSeconds(30);

        [ImportingConstructor]
        public InterruptAndRetryWhenRMSAbove(IGuiderMediator guiderMediator) {
            this.guiderMediator = guiderMediator;
        }

        public InterruptAndRetryWhenRMSAbove(InterruptAndRetryWhenRMSAbove copyMe) : this(copyMe.guiderMediator) {
            CopyMetaData(copyMe);
        }

        public override object Clone() {
            return new InterruptAndRetryWhenRMSAbove(this) {
                RmsThreshold = this.RmsThreshold,
                Mode = this.Mode,
                MinimumPoints = this.MinimumPoints,
                RecoveryTimeout = this.RecoveryTimeout,
                StableTime = this.StableTime,
                MaxRetries = this.MaxRetries
            };
        }

        [ObservableProperty]
        [property: JsonProperty]
        private double rmsThreshold = 1;

        [ObservableProperty]
        [property: JsonProperty]
        private int minimumPoints = 5;

        [ObservableProperty]
        [property: JsonProperty]
        private GuideInterrupteMode mode = GuideInterrupteMode.Peak;

        /// <summary>Maximum time in minutes to wait for the guiding to recover before the exposure is repeated anyways. Zero waits indefinitely</summary>
        [ObservableProperty]
        [property: JsonProperty]
        private double recoveryTimeout = 5;

        /// <summary>Time in seconds the guiding has to stay below the threshold before the exposure is repeated</summary>
        [ObservableProperty]
        [property: JsonProperty]
        private double stableTime = 15;

        /// <summary>Maximum amount of repetitions of the same exposure. Zero means unlimited</summary>
        [ObservableProperty]
        [property: JsonProperty]
        private int maxRetries = 3;

        [ObservableProperty]
        private RMS rmsInstance;

        [ObservableProperty]
        private bool isWaitingForRecovery;

        [ObservableProperty]
        private string recoveryStatus = string.Empty;

        private readonly object rmsLock = new object();
        private readonly Stopwatch stableSince = new Stopwatch();
        private DateTime lastStepAt = DateTime.MinValue;
        private bool watchdogArmed;
        private bool waitingForRecovery;
        private IExposureItem exposureItem;
        private ISequenceItem pendingRetryItem;
        private volatile bool skippedByTrigger;
        private volatile bool retryInProgress;

        private double SafeRecoveryTimeout => Math.Max(0, RecoveryTimeout);
        private double SafeStableTime => Math.Max(0, StableTime);
        private int SafeMaxRetries => Math.Max(0, MaxRetries);

        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) {
            // While we are repeating an exposure ourselves the watchdog is armed manually - do not interfere here
            if (retryInProgress) { return false; }

            if (nextItem is IExposureItem exp) {
                ArmWatchdog(exp);
            } else {
                Disarm();
            }

            // This trigger does not act before an item, it only reacts after an interrupted exposure
            return false;
        }

        public override bool ShouldTriggerAfter(ISequenceItem previousItem, ISequenceItem nextItem) {
            if (retryInProgress) { return false; }

            if (skippedByTrigger && previousItem != null && ReferenceEquals(previousItem, exposureItem)) {
                skippedByTrigger = false;
                pendingRetryItem = previousItem;
                return true;
            }

            skippedByTrigger = false;
            return false;
        }

        public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            var item = pendingRetryItem;
            pendingRetryItem = null;
            if (item == null) { return; }

            retryInProgress = true;
            try {
                var attempt = 0;
                while (true) {
                    attempt++;

                    await WaitForRecovery(progress, token);

                    Logger.Info($"Repeating interrupted exposure (attempt {attempt}), item status was {item.Status}");
                    Notification.ShowInformation($"Guiding recovered - repeating interrupted exposure (attempt {attempt})");

                    ResetItem(item);
                    skippedByTrigger = false;
                    ArmWatchdog(item as IExposureItem);

                    // Run the whole trigger pipeline for the repeated exposure so a due meridian flip
                    // or autofocus is not silently skipped. Our own hooks bail out on retryInProgress.
                    var triggerable = context as ITriggerable;
                    if (triggerable != null) { await triggerable.RunTriggers(item, item, progress, token); }
                    await item.Run(progress, token);
                    if (triggerable != null) { await triggerable.RunTriggersAfter(item, item, progress, token); }

                    Disarm();

                    if (!skippedByTrigger) {
                        // Exposure completed without being interrupted again
                        break;
                    }
                    skippedByTrigger = false;

                    if (SafeMaxRetries > 0 && attempt >= SafeMaxRetries) {
                        Logger.Warning($"Exposure was interrupted {attempt} times - giving up and continuing with the sequence");
                        Notification.ShowWarning($"Exposure was interrupted {attempt} times - continuing with the sequence");
                        break;
                    }
                }
            } finally {
                retryInProgress = false;
                pendingRetryItem = null;
                IsWaitingForRecovery = false;
                RecoveryStatus = string.Empty;
                Disarm();
            }
        }

        /// <summary>
        /// Waits until the guiding stayed below the threshold for the configured stable time.
        /// A disconnected guider or one that does not deliver guide steps never counts as recovered,
        /// otherwise the sequence would happily continue unguided.
        /// When the recovery timeout elapses the wait is aborted and the exposure is repeated anyways
        /// so the sequence does not stall - the reason is logged.
        /// </summary>
        private async Task WaitForRecovery(IProgress<ApplicationStatus> progress, CancellationToken token) {
            IsWaitingForRecovery = true;
            var overallTimeout = Stopwatch.StartNew();
            var timeout = TimeSpan.FromMinutes(SafeRecoveryTimeout);
            var stableSpan = TimeSpan.FromSeconds(SafeStableTime);

            lock (rmsLock) {
                waitingForRecovery = true;
                watchdogArmed = false;
                ResetMeasurement();
            }

            try {
                while (!token.IsCancellationRequested) {
                    var recovered = false;
                    string blockedBy;

                    lock (rmsLock) {
                        if (!guiderMediator.GetInfo().Connected) {
                            blockedBy = "guider not connected";
                            ResetMeasurement();
                        } else if (DateTime.UtcNow - lastStepAt > GuideStepTimeout) {
                            blockedBy = "no guide steps received";
                            ResetMeasurement();
                        } else if (RmsInstance.DataPoints < RequiredDataPoints()) {
                            blockedBy = "not enough guide steps yet";
                        } else if (stableSince.Elapsed < stableSpan) {
                            blockedBy = "waiting for the guiding to stay calm";
                        } else {
                            blockedBy = null;
                            recovered = true;
                        }
                    }

                    if (recovered) {
                        Logger.Info($"Guiding stayed below the threshold for {SafeStableTime}s");
                        return;
                    }

                    if (SafeRecoveryTimeout > 0 && overallTimeout.Elapsed > timeout) {
                        Logger.Warning($"Guiding did not recover within {SafeRecoveryTimeout} minutes ({blockedBy}) - repeating the exposure anyways");
                        Notification.ShowWarning($"Guiding did not recover within {SafeRecoveryTimeout} minutes ({blockedBy}) - repeating the exposure anyways");
                        return;
                    }

                    double remaining;
                    lock (rmsLock) {
                        remaining = Math.Max(0, (stableSpan - stableSince.Elapsed).TotalSeconds);
                    }
                    RecoveryStatus = $"Waiting for guiding to recover ({Math.Round(remaining)}s) - {blockedBy}";
                    progress?.Report(new ApplicationStatus() { Status = RecoveryStatus });

                    await Task.Delay(1000, token);
                }
                token.ThrowIfCancellationRequested();
            } finally {
                lock (rmsLock) {
                    waitingForRecovery = false;
                }
                IsWaitingForRecovery = false;
                RecoveryStatus = string.Empty;
                progress?.Report(new ApplicationStatus() { Status = string.Empty });
            }
        }

        /// <summary>How many guide steps the current mode needs before its values mean anything</summary>
        private int RequiredDataPoints() {
            return Mode == GuideInterrupteMode.RMS ? Math.Max(1, MinimumPoints + 1) : 1;
        }

        /// <summary>Throws away all collected guide steps and restarts the stable window. Caller holds rmsLock</summary>
        private void ResetMeasurement() {
            if (RmsInstance == null) {
                RmsInstance = new RMS();
            } else {
                RmsInstance.Clear();
            }

            var scale = guiderMediator.GetInfo().PixelScale;
            if (scale > 0) { RmsInstance.SetScale(scale); }

            stableSince.Restart();
        }

        private static void ResetItem(ISequenceItem item) {
            if (item is ISequenceContainer container) {
                container.ResetAll();
            } else {
                item.ResetProgress();
            }
        }

        private void ArmWatchdog(IExposureItem exp) {
            if (exp == null) { return; }

            exposureItem = exp;
            skippedByTrigger = false;
            lock (rmsLock) {
                waitingForRecovery = false;
                ResetMeasurement();
                watchdogArmed = true;
            }
        }

        private void Disarm() {
            lock (rmsLock) {
                watchdogArmed = false;
                waitingForRecovery = false;
                RmsInstance = null;
            }
        }

        private void GuiderMediator_GuideEvent(object sender, IGuideStep e) {
            var interrupt = false;
            string reason = null;

            try {
                lock (rmsLock) {
                    lastStepAt = DateTime.UtcNow;
                    if (RmsInstance == null) { return; }

                    RmsInstance.AddDataPoint(e.RADistanceRaw, e.DECDistanceRaw);
                    var above = RmsThresholdEvaluator.IsAboveThreshold(RmsInstance, Mode, RmsThreshold, MinimumPoints, out reason);

                    if (waitingForRecovery) {
                        if (above) {
                            // Still bad - the stable window starts over
                            RmsInstance.Clear();
                            stableSince.Restart();
                        }
                    } else if (watchdogArmed && above) {
                        // Disarm inside the lock so no second guide step can skip as well
                        watchdogArmed = false;
                        interrupt = true;
                    }
                }
            } catch (Exception ex) {
                Logger.Error(ex);
                return;
            }

            if (!interrupt) { return; }

            var exp = exposureItem;
            // Re-read the status as late as possible. There is no atomic "skip if still running" in
            // NINA, so a exposure that finishes in this very moment can still be marked as skipped
            // and would then be exposed a second time.
            if (exp == null || exp.Status != NINA.Core.Enum.SequenceEntityStatus.RUNNING) {
                Logger.Info($"{reason} - exposure is no longer running, not interrupting");
                return;
            }

            Notification.ShowInformation($"{reason} - Interrupting current exposure");
            Logger.Info($"{reason} - Interrupting running exposure item, it will be repeated once the guiding has recovered");
            skippedByTrigger = true;
            exp.Skip();
        }

        private void RegisterGuideEvent() {
            guiderMediator.GuideEvent -= GuiderMediator_GuideEvent;
            if (ItemUtility.IsInRootContainer(Parent)) {
                guiderMediator.GuideEvent += GuiderMediator_GuideEvent;
            }
        }

        private void UnregisterGuideEvent() {
            guiderMediator.GuideEvent -= GuiderMediator_GuideEvent;
        }

        public override void AfterParentChanged() {
            RegisterGuideEvent();
            if (!ItemUtility.IsInRootContainer(this.Parent)) {
                // When item is removed from sequencer
                Disarm();
            }
            base.AfterParentChanged();
        }

        public override void SequenceBlockStarted() {
            RegisterGuideEvent();
            base.SequenceBlockStarted();
        }

        public override void SequenceBlockFinished() {
            RegisterGuideEvent();
            Disarm();
            base.SequenceBlockFinished();
        }

        public override void SequenceBlockTeardown() {
            UnregisterGuideEvent();
            Disarm();
            base.SequenceBlockTeardown();
        }

        public override void Teardown() {
            UnregisterGuideEvent();
            Disarm();
            base.Teardown();
        }

        /// <summary>
        /// This string will be used for logging
        /// </summary>
        /// <returns></returns>
        public override string ToString() {
            return $"Category: {Category}, Trigger: {nameof(InterruptAndRetryWhenRMSAbove)}, Mode {Mode}, Threshold {RmsThreshold}, Points {MinimumPoints}, RecoveryTimeout {RecoveryTimeout}, StableTime {StableTime}, MaxRetries {MaxRetries}";
        }
    }
}
