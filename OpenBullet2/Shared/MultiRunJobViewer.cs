﻿using Blazored.Modal;
using Blazored.Modal.Services;
using GridBlazor;
using GridBlazor.Pages;
using GridMvc.Server;
using GridShared;
using GridShared.Utility;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.JSInterop;
using Newtonsoft.Json;
using OpenBullet2.Helpers;
using OpenBullet2.Logging;
using OpenBullet2.Models.Data;
using OpenBullet2.Models.Jobs;
using OpenBullet2.Repositories;
using OpenBullet2.Services;
using OpenBullet2.Shared.Forms;
using RuriLib.Logging;
using RuriLib.Models.Hits;
using RuriLib.Models.Jobs;
using RuriLib.Threading.Models;
using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenBullet2.Shared
{
    public partial class MultiRunJobViewer : IDisposable
    {
        [Parameter] public MultiRunJob Job { get; set; }

        [Inject] private IModalService Modal { get; set; }
        [Inject] private VolatileSettingsService VolatileSettings { get; set; }
        [Inject] private PersistentSettingsService PersistentSettings { get; set; }
        [Inject] private MemoryJobLogger Logger { get; set; }
        [Inject] private IJobRepository JobRepo { get; set; }
        [Inject] private JobManagerService JobManager { get; set; }
        [Inject] private NavigationManager Nav { get; set; }

        private bool changingBots = false;
        private Hit selectedHit;
        private Timer uiRefreshTimer;

        private GridComponent<Hit> gridComponent;
        private CGrid<Hit> grid;
        private Task gridLoad;

        protected override async Task OnParametersSetAsync()
        {
            Action<IGridColumnCollection<Hit>> columns = c =>
            {
                c.Add(h => h.Date).Titled(Loc["Date"]);
                c.Add(h => h.DataString).Titled(Loc["Data"]);
                c.Add(h => h.Proxy).Titled(Loc["Proxy"]);
                c.Add(h => h.Type).Titled(Loc["Type"]);
                c.Add(h => h.CapturedDataString).Titled(Loc["CapturedData"]);
            };

            var query = new QueryDictionary<StringValues>();
            query.Add("grid-page", "2");

            var client = new GridClient<Hit>(q => GetGridRows(columns, q), query, false, "hitsGrid", columns, CultureInfo.CurrentCulture)
                .Sortable()
                .Filterable()
                .WithMultipleFilters()
                .SetKeyboard(true)
                .ChangePageSize(true)
                .Selectable(true, false, true);
            grid = client.Grid;

            // Try to set a previous filter
            if (VolatileSettings.GridQueries.ContainsKey("hitsGrid"))
            {
                grid.Query = VolatileSettings.GridQueries["hitsGrid"];
            }

            // Set new items to grid
            gridLoad = client.UpdateGrid();
            await gridLoad;
        }

        private ItemsDTO<Hit> GetGridRows(Action<IGridColumnCollection<Hit>> columns,
                QueryDictionary<StringValues> query)
        {
            VolatileSettings.GridQueries["hitsGrid"] = query;

            var server = new GridServer<Hit>(Job.Hits, new QueryCollection(query),
                true, "hitsGrid", columns, 15).Sortable().Filterable().WithMultipleFilters();

            // Return items to displays
            return server.ItemsToDisplay;
        }

        protected override void OnInitialized() => AddEventHandlers();

        protected override void OnAfterRender(bool firstRender)
        {
            if (firstRender)
            {
                var interval = Math.Max(50, PersistentSettings.OpenBulletSettings.GeneralSettings.JobUpdateInterval);
                uiRefreshTimer = new Timer(new TimerCallback(async _ => await InvokeAsync(StateHasChanged)),
                    null, interval, interval);
            }
        }

        protected void OnHitSelected(object item)
        {
            if (item.GetType() == typeof(Hit))
            {
                selectedHit = (Hit)item;
            }
        }

        private async Task ChangeBots()
        {
            var parameters = new ModalParameters();
            parameters.Add(nameof(BotsSelector.Bots), Job.Bots);

            var modal = Modal.Show<BotsSelector>(Loc["EditBotsAmount"], parameters);
            var result = await modal.Result;

            if (!result.Cancelled)
            {
                var newAmount = (int)result.Data;
                changingBots = true;

                await Job.ChangeBots(newAmount);

                Job.Bots = newAmount;
                changingBots = false;
            }
        }

        private void ChangeOptions()
        {
            Nav.NavigateTo($"jobs/edit/{Job.Id}");
        }

        private void LogResult(object sender, ResultDetails<MultiRunInput, CheckResult> details)
        {
            var botData = details.Result.BotData;
            var data = botData.Line.Data;
            var proxy = botData.Proxy != null
                ? $"{botData.Proxy.Host}:{botData.Proxy.Port}"
                : string.Empty;

            var message = string.Format(Loc["LineCheckedMessage"], data, proxy, botData.STATUS);
            var color = botData.STATUS switch
            {
                "SUCCESS" => "yellowgreen",
                "FAIL" => "tomato",
                "BAN" => "plum",
                "RETRY" => "yellow",
                "ERROR" => "red",
                "NONE" => "skyblue",
                _ => "orange"
            };
            Logger.Log(Job.Id, message, LogKind.Custom, color);
        }

        private void PlaySoundOnHit(object sender, ResultDetails<MultiRunInput, CheckResult> details)
        {
            if (details.Result.BotData.STATUS == "SUCCESS" && PersistentSettings.OpenBulletSettings.CustomizationSettings.PlaySoundOnHit)
            {
                _ = js.InvokeVoidAsync("playHitSound");
            }
        }

        private void LogError(object sender, Exception ex)
        {
            Logger.LogError(Job.Id, $"{Loc["TaskManagerError"]} {ex.Message}");
        }

        private void LogTaskError(object sender, ErrorDetails<MultiRunInput> details)
        {
            var data = details.Item.BotData.Line.Data;
            var proxy = details.Item.BotData.Proxy != null
                ? $"{details.Item.BotData.Proxy.Host}:{details.Item.BotData.Proxy.Port}"
                : string.Empty;
            Logger.LogError(Job.Id, $"{Loc["TaskError"]} ({data})({proxy})! {details.Exception.Message}");
        }

        private void LogCompleted(object sender, EventArgs e)
        {
            Logger.LogInfo(Job.Id, Loc["TaskManagerCompleted"]);
        }

        private void SaveRecord(object sender, EventArgs e)
        {
            // Fire and forget
            JobManager.SaveRecord(Job).ConfigureAwait(false);
        }

        private void SaveJobOptions(object sender, EventArgs e)
        {
            // Get the job
            var job = JobRepo.Get(Job.Id).Result;
            
            // Deserialize and unwrap the job options
            var settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto };
            var wrapper = JsonConvert.DeserializeObject<JobOptionsWrapper>(job.JobOptions, settings);
            var options = ((MultiRunJobOptions)wrapper.Options);
            
            // Check if it's valid
            if (string.IsNullOrEmpty(options.ConfigId))
            {
                Console.WriteLine("Skipped job options save because ConfigId was null");
                return;
            }

            if (options.DataPool is WordlistDataPoolOptions x && x.WordlistId == -1)
            {
                Console.WriteLine("Skipped job options save because WordlistId was -1");
                return;
            }

            // Update the skip
            options.Skip = Job.Skip + Job.DataTested;
            
            // Wrap and serialize again
            var newWrapper = new JobOptionsWrapper { Options = options };
            job.JobOptions = JsonConvert.SerializeObject(newWrapper, settings);

            // Update the job
            JobRepo.Update(job).ConfigureAwait(false);
        }

        private async Task Start()
        {
            try
            {
                await AskCustomInputs();

                Logger.LogInfo(Job.Id, Loc["StartedWaiting"]);
                await Job.Start();
                Logger.LogInfo(Job.Id, Loc["StartedChecking"]);
            }
            catch (Exception ex)
            {
                await js.AlertException(ex);
            }
        }

        private async Task Stop()
        {
            try
            {
                Logger.LogInfo(Job.Id, Loc["SoftStopMessage"]);
                await Job.Stop();
            }
            catch (Exception ex)
            {
                await js.AlertException(ex);
            }
        }

        private async Task Abort()
        {
            try
            {
                Logger.LogInfo(Job.Id, Loc["HardStopMessage"]);
                await Job.Abort();
            }
            catch (Exception ex)
            {
                await js.AlertException(ex);
            }
        }

        private async Task Pause()
        {
            try
            {
                Logger.LogInfo(Job.Id, Loc["PauseMessage"]);
                await Job.Pause();
                Logger.LogInfo(Job.Id, Loc["TaskManagerPaused"]);
            }
            catch (Exception ex)
            {
                await js.AlertException(ex);
            }
        }

        private async Task Resume()
        {
            try
            {
                await Job.Resume();
                Logger.LogInfo(Job.Id, Loc["ResumeMessage"]);
            }
            catch (Exception ex)
            {
                await js.AlertException(ex);
            }
        }

        private async Task SkipWait()
        {
            try
            {
                Job.SkipWait();
                Logger.LogInfo(Job.Id, Loc["SkippedWait"]);
            }
            catch (Exception ex)
            {
                await js.AlertException(ex);
            }
        }

        private async Task AskCustomInputs()
        {
            Job.CustomInputsAnswers.Clear();
            foreach (var input in Job.Config.Settings.InputSettings.CustomInputs)
            {
                var parameters = new ModalParameters();
                parameters.Add(nameof(CustomInputQuestion.Question), input.Description);
                parameters.Add(nameof(CustomInputQuestion.Answer), input.DefaultAnswer);

                var modal = Modal.Show<CustomInputQuestion>(Loc["CustomInput"], parameters);
                var result = await modal.Result;
                var answer = result.Cancelled ? input.DefaultAnswer : (string)result.Data;
                Job.CustomInputsAnswers[input.VariableName] = answer;
            }
        }

        private async Task RefreshHits()
        {
            await gridComponent.UpdateGrid();
            StateHasChanged();
        }

        private async Task CopyHitDataCapture()
        {
            var selectedItems = grid.SelectedItems.Cast<Hit>().ToList();

            if (selectedItems.Count == 0)
            {
                await ShowNoHitSelectedWarning();
                return;
            }

            var sb = new StringBuilder();
            selectedItems.ForEach(i => sb.AppendLine($"{i.Data.Data} | {i.CapturedDataString}"));

            await js.CopyToClipboard(sb.ToString());
        }

        private async Task SendToDebugger()
        {
            if (selectedHit == null)
            {
                await ShowNoHitSelectedWarning();
                return;
            }

            VolatileSettings.DebuggerOptions.TestData = selectedHit.Data.Data;
            VolatileSettings.DebuggerOptions.WordlistType = selectedHit.Data.Type.Name;
            VolatileSettings.DebuggerOptions.UseProxy = selectedHit.Proxy != null;

            if (selectedHit.Proxy != null)
            {
                VolatileSettings.DebuggerOptions.ProxyType = selectedHit.Proxy.Type;
                VolatileSettings.DebuggerOptions.TestProxy = selectedHit.Proxy.ToString();
            }
        }

        private async Task ShowFullLog()
        {
            if (selectedHit == null)
            {
                await ShowNoHitSelectedWarning();
                return;
            }

            if (selectedHit.BotLogger == null)
            {
                await js.AlertError(Loc["Disabled"], Loc["BotLogDisabledError"]);
                return;
            }

            var parameters = new ModalParameters();
            parameters.Add(nameof(BotLoggerViewerModal.BotLogger), selectedHit.BotLogger);

            Modal.Show<BotLoggerViewerModal>(Loc["BotLog"], parameters);
        }

        private async Task ShowNoHitSelectedWarning()
            => await js.AlertError(Loc["Uh-Oh"], Loc["NoHitSelectedWarning"]);

        private void AddEventHandlers()
        {
            if (PersistentSettings.OpenBulletSettings.GeneralSettings.EnableJobLogging)
            {
                Job.OnResult += LogResult;
                Job.OnResult += PlaySoundOnHit;
                Job.OnTaskError += LogTaskError;
                Job.OnError += LogError;
                Job.OnCompleted += LogCompleted;
            }
            
            Job.OnCompleted += SaveRecord;
            Job.OnTimerTick += SaveRecord;
            Job.OnTimerTick += SaveJobOptions;
        }

        private void RemoveEventHandlers()
        {
            try
            {
                Job.OnResult -= LogResult;
                Job.OnResult -= PlaySoundOnHit;
                Job.OnTaskError -= LogTaskError;
                Job.OnError -= LogError;
                Job.OnCompleted -= LogCompleted;
            }
            catch
            {

            }

            try
            {
                Job.OnCompleted -= SaveRecord;
                Job.OnTimerTick -= SaveRecord;
                Job.OnTimerTick -= SaveJobOptions;
            }
            catch 
            {

            }
        }

        public void Dispose()
        {
            uiRefreshTimer?.Dispose();
            RemoveEventHandlers();
        }
    }
}
