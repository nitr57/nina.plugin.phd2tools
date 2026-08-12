using CommunityToolkit.Mvvm.ComponentModel;
using Newtonsoft.Json;
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
    [ExportMetadata("Description", "This trigger will interrupt an exposure when a guide pulse exceeds a certain value, wait until the guiding has recovered and then repeat the very same exposure instead of continuing with the next instruction")]
    [ExportMetadata("Icon", "PhdTools_Eye")]
    [ExportMetadata("Category", "Phd2 Tools")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public partial class InterruptAndRetryWhenRMSAbove : SequenceTrigger {
        private IGuiderMediator guiderMediator;

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

        /// <summary>Maximum time in minutes to wait for the guiding to recover before the exposure is repeated anyways</summary>
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

        private Guid activeRMSRecording;
        private IExposureItem exposureItem;
        private ISequenceItem pendingRetryItem;
        private volatile bool skippedByTrigger;
        private volatile bool retryInProgress;
        private CancellationTokenSource workerCts;
        private readonly object workerLock = new object();

        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) {
            // While we are repeating an exposure ourselves the watchdog is armed manually - do not interfere here
            if (retryInProgress) { return false; }

            if (nextItem is IExposureItem exp) {
                ArmWatchdog(exp);
            } else {
                StopRecording();
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

                    Logger.Info($"Repeating interrupted exposure (attempt {attempt})");
                    Notification.ShowInformation($"Guiding recovered - repeating interrupted exposure (attempt {attempt})");

                    ResetItem(item);
                    skippedByTrigger = false;
                    ArmWatchdog(item as IExposureItem);

                    await item.Run(progress, token);

                    StopRecording();

                    if (!skippedByTrigger) {
                        // Exposure completed without being interrupted again
                        break;
                    }
                    skippedByTrigger = false;

                    if (MaxRetries > 0 && attempt >= MaxRetries) {
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
                StopRecording();
            }
        }

        /// <summary>
        /// Waits until the guiding stayed below the threshold for the configured stable time.
        /// A fresh RMS recording is used so that the values that caused the interruption are not taken into account.
        /// When the recovery timeout elapses the wait is aborted and the exposure is repeated anyways so the sequence does not stall.
        /// </summary>
        private async Task WaitForRecovery(IProgress<ApplicationStatus> progress, CancellationToken token) {
            IsWaitingForRecovery = true;
            var overallTimeout = Stopwatch.StartNew();
            var stable = Stopwatch.StartNew();
            var timeout = TimeSpan.FromMinutes(RecoveryTimeout);
            var stableSpan = TimeSpan.FromSeconds(StableTime);

            StartRecording();
            try {
                while (!token.IsCancellationRequested) {
                    if (RecoveryTimeout > 0 && overallTimeout.Elapsed > timeout) {
                        Logger.Warning($"Guiding did not recover within {RecoveryTimeout} minutes - repeating the exposure anyways");
                        Notification.ShowWarning($"Guiding did not recover within {RecoveryTimeout} minutes - repeating the exposure anyways");
                        return;
                    }

                    if (guiderMediator.GetInfo().Connected
                        && RmsInstance != null
                        && RmsThresholdEvaluator.IsAboveThreshold(RmsInstance, Mode, RmsThreshold, MinimumPoints, out var reason)) {
                        // Still bad - restart the recording so the stable window starts over
                        Logger.Debug($"Waiting for guiding to recover - {reason}");
                        StopRecording();
                        StartRecording();
                        stable.Restart();
                    } else if (stable.Elapsed >= stableSpan) {
                        Logger.Info($"Guiding stayed below the threshold for {StableTime}s");
                        return;
                    }

                    var remaining = Math.Max(0, (stableSpan - stable.Elapsed).TotalSeconds);
                    RecoveryStatus = $"Waiting for guiding to recover ({Math.Round(remaining)}s)";
                    progress?.Report(new ApplicationStatus() { Status = RecoveryStatus });

                    await Task.Delay(1000, token);
                }
                token.ThrowIfCancellationRequested();
            } finally {
                IsWaitingForRecovery = false;
                RecoveryStatus = string.Empty;
                progress?.Report(new ApplicationStatus() { Status = string.Empty });
            }
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
            StartRecording();

            lock (workerLock) {
                CancelWorker();
                workerCts = new CancellationTokenSource();
                _ = BackgroundWorker(workerCts.Token);
            }
        }

        private void StartRecording() {
            if (activeRMSRecording != Guid.Empty) {
                guiderMediator.StopRMSRecording(activeRMSRecording);
            }
            activeRMSRecording = guiderMediator.StartRMSRecording();
            RmsInstance = guiderMediator.GetRMSRecording(activeRMSRecording);
        }

        private void StopRecording() {
            if (activeRMSRecording != Guid.Empty) {
                guiderMediator.StopRMSRecording(activeRMSRecording);
            }
            activeRMSRecording = Guid.Empty;
            RmsInstance = null;
            lock (workerLock) {
                CancelWorker();
            }
        }

        private void CancelWorker() {
            try {
                workerCts?.Cancel();
                workerCts?.Dispose();
            } catch { } finally {
                workerCts = null;
            }
        }

        private Task BackgroundWorker(CancellationToken token) {
            return Task.Run(async () => {
                while (!token.IsCancellationRequested && this.Parent?.Status == NINA.Core.Enum.SequenceEntityStatus.RUNNING) {
                    try {
                        if (activeRMSRecording != Guid.Empty && guiderMediator.GetInfo().Connected
                            && RmsThresholdEvaluator.IsAboveThreshold(RmsInstance, Mode, RmsThreshold, MinimumPoints, out var reason)) {
                            if (exposureItem != null && exposureItem.Status == NINA.Core.Enum.SequenceEntityStatus.RUNNING) {
                                Notification.ShowInformation($"{reason} - Interrupting current exposure");
                                Logger.Info($"{reason} - Interrupting running exposure item, it will be repeated once the guiding has recovered");
                                skippedByTrigger = true;
                                exposureItem.Skip();
                                return;
                            }
                        }
                        await Task.Delay(1000, token);
                    } catch (OperationCanceledException) {
                        return;
                    } catch (Exception ex) {
                        Logger.Error(ex);
                    }
                }
            });
        }

        public override void AfterParentChanged() {
            if (!ItemUtility.IsInRootContainer(this.Parent)) {
                // When item is removed from sequencer
                StopRecording();
            }
            base.AfterParentChanged();
        }

        public override void SequenceBlockFinished() {
            StopRecording();
            base.SequenceBlockFinished();
        }

        public override void SequenceBlockTeardown() {
            StopRecording();
            base.SequenceBlockTeardown();
        }

        public override void Teardown() {
            StopRecording();
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
