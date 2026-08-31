using CommunityToolkit.Mvvm.ComponentModel;
using Newtonsoft.Json;
using NINA.Core.Interfaces;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Utility;
using NINA.WPF.Base.Mediator;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace nina.plugin.phd2tools.Phd2ToolsSequenceItems {

    [ExportMetadata("Name", "Interrupt when RMS above")]
    [ExportMetadata("Description", "This trigger will interrupt an exposure when a guide pulse exceeds a certain value")]
    [ExportMetadata("Icon", "PhdTools_Eye")]
    [ExportMetadata("Category", "Phd2 Tools")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public partial class InterruptWhenRMSAbove : SequenceTrigger {
        private IGuiderMediator guiderMediator;

        [ImportingConstructor]
        public InterruptWhenRMSAbove(IGuiderMediator guiderMediator) {
            this.guiderMediator = guiderMediator;

            workerCts = new CancellationTokenSource();
        }

        public InterruptWhenRMSAbove(InterruptWhenRMSAbove copyMe) : this(copyMe.guiderMediator) {
            CopyMetaData(copyMe);
        }

        public override object Clone() {
            return new InterruptWhenRMSAbove(this) {
                RmsThreshold = this.RmsThreshold,
                Mode = this.Mode,
                MinimumPoints = this.MinimumPoints
            };
        }

        public override Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            // This trigger is not actively executing but rather a background watchdog
            return Task.CompletedTask;
        }

        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) {
            if (nextItem is IExposureItem exp) {
                // Start recording and background work when exposure is about to start
                activeRMSRecording = guiderMediator.StartRMSRecording();

                RmsInstance = guiderMediator.GetRMSRecording(activeRMSRecording);
                exposureItem = exp;

                lock (workerLock) {
                    try {
                        workerCts?.Cancel();
                        workerCts?.Dispose();
                    } catch { }
                    workerCts = new CancellationTokenSource();
                    _ = BackgroundWorker(workerCts.Token);
                }
            } else {
                StopRecording();
            }

            // This trigger is not actively executing but rather a background watchdog
            return false;
        }

        public override void AfterParentChanged() {
            if (!ItemUtility.IsInRootContainer(this.Parent)) {
                // When item is removed from sequencer
                StopRecording();
            }
            base.AfterParentChanged();
        }

        private void StopRecording() {
            // Stop recording and background work when exposure is finished
            if (activeRMSRecording != Guid.Empty) {
                guiderMediator.StopRMSRecording(activeRMSRecording);
            }
            activeRMSRecording = Guid.Empty;
            RmsInstance = null;
            lock (workerLock) {
                try {
                    workerCts?.Cancel();
                    workerCts?.Dispose();
                } catch { }
            }
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

        [ObservableProperty]
        [property: JsonProperty]
        private double rmsThreshold = 1;

        [ObservableProperty]
        [property: JsonProperty]
        private int minimumPoints = 5;

        [ObservableProperty]
        [property: JsonProperty]
        private GuideInterrupteMode mode = GuideInterrupteMode.Peak;

        [ObservableProperty]
        private RMS rmsInstance;

        private Guid activeRMSRecording;
        private IExposureItem exposureItem;
        private CancellationTokenSource workerCts;
        private readonly object workerLock = new object();

        private Task BackgroundWorker(CancellationToken token) {
            return Task.Run(async () => {
                while (!token.IsCancellationRequested && this.Parent?.Status == NINA.Core.Enum.SequenceEntityStatus.RUNNING) {
                    try {
                        if (activeRMSRecording != Guid.Empty && guiderMediator.GetInfo().Connected
                            && RmsThresholdEvaluator.IsAboveThreshold(RmsInstance, Mode, RmsThreshold, MinimumPoints, out var reason)) {
                            Notification.ShowInformation($"{reason} - Interrupting current exposure");
                            Logger.Info(reason);

                            if (exposureItem != null && exposureItem.Status == NINA.Core.Enum.SequenceEntityStatus.RUNNING) {
                                Logger.Info("Interrupting running exposure item");
                                exposureItem?.Skip();
                            }
                        }
                        await Task.Delay(1000, token);
                    } catch (OperationCanceledException) {
                    } catch (Exception ex) {
                        Logger.Error(ex);
                    }
                }
            });
        }

        /// <summary>
        /// This string will be used for logging
        /// </summary>
        /// <returns></returns>
        public override string ToString() {
            return $"Category: {Category}, Trigger: {nameof(InterruptWhenRMSAbove)}, Mode {Mode}, Threshold {RmsThreshold}, Points {MinimumPoints}";
        }
    }

    public enum GuideInterrupteMode {
        Peak,
        RMS
    }
}