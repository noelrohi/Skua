# Mac App: parity with the Windows app (#94)

This checklist compares the Mac App with the Windows app, feature by feature. It covers every main-menu item (`MainMenuViewModel`), window, panel and dialog in `Skua.App.WPF`, `Skua.WPF` and `Skua.Manager`, and every view model in `Skua.Core/ViewModels/**` that has a WPF view. The design is in [ADR 0006](../adr/0006-mac-app-hosts-the-engine.md).

The status column uses these values:

- **works**: the Mac App has it, and a test opens it and does its main action against the fake Game Host (or the fake Keychain, GitHub or servers API). The test is named.
- **present**: the Mac App has it, and a test opens it and binds it, but no test drives its main action. The note says why.
- **gap**: the Mac App lacks it. The linked issue brings it.
- **in progress**: its ticket is still open.
- **deliberate**: a decided difference. The note gives the reason.

Tests are in `Skua.Avalonia.Tests` and named `Class.Method`. `ViewCoverageTests` keeps the list honest. It fails if a Core view model with a WPF view has no Avalonia view, unless this page lists that view model as a deliberate difference or a gap. It also opens every window the Windows app registers (`ManagedWindows.Register`) through the Mac App's window service, and checks that each window shows its view.

## Gaps

None open: every gap the audit found is closed.

## Main window (`Skua.App.WPF/MainWindow.xaml`)

| Windows | Mac App | Status | Evidence or reason |
|---|---|---|---|
| Title: `MainViewModel.Title`, with the username when Show Username in Title is on | `MainViewModel.Title` from the app's container, with this build's version recorded at start, and the Engine Name after it unless it is `default` | works | `MainWindowTitleTests.The_title_is_Skua_and_the_builds_version_over_an_older_one_and_names_an_Engine_other_than_default`, `MainWindowTitleTests.With_Show_Username_in_Title_on_the_title_ends_with_the_username_after_a_login_and_turning_it_off_removes_it_for_the_next_start_too`. The Engine Name, as in `Skua - 1.4.4.4 : SkuaTester (Engine alice)`, is deliberate: it tells apart the apps the Skua Manager launches in the Window menu and with ⌘`. |
| Game (`GameContainerUserControl`, the Flash ActiveX host) | Game View (`GameView`) over the Game Host's Frame Buffer | works | `GameViewTests.Shows_the_Game_Hosts_frames_as_they_advance`, `GameViewTests.Keys_and_typed_text_arrive_in_order` |
| Game polish: sharpness, cursor, clipboard | Retina rendering, the hand cursor, ⌘C and ⌘V in chat, the wheel by lines or pixels | works | `GameViewTests.On_a_Retina_display_the_Game_Host_renders_at_the_views_device_pixels_and_they_are_drawn_one_to_one`, `GameViewTests.The_cursor_changes_over_the_games_buttons`, `GameViewTests.Command_V_pastes_the_Macs_clipboard_into_the_game_after_the_keys_before_it`, `GameViewTests.Text_the_game_copies_goes_on_the_Macs_clipboard` |
| Main menu (`MainMenuUserControl`) | The in-window menu and the macOS menu bar, both built from `MainMenuViewModel` | works | `PanelTests.The_main_menu_enables_the_panels_with_views_and_the_items_with_their_own_command_and_shows_the_rest_disabled` |
| Closing the window exits the app | Closing hides the window, and the Engine keeps playing. The Dock icon reopens it, and ⌘Q quits. | deliberate | ADR 0006: the app owns its Engine. `CloseAndQuitTests.Closing_the_window_hides_it_the_Game_View_goes_headless_and_skua_status_keeps_working_until_it_reopens` |
| Tray icon: double-click, Toggle Bot | The Dock icon reopens the window | works | `CloseAndQuitTests.Closing_the_window_hides_it_the_Game_View_goes_headless_and_skua_status_keeps_working_until_it_reopens` |
| Tray icon: Exit | ⌘Q, or Quit in the Dock menu, asking first while a Script runs | works | `CloseAndQuitTests.Quitting_while_a_Script_runs_asks_first_and_Cancel_keeps_playing`, `CloseAndQuitTests.Quitting_with_no_Script_running_quits_without_asking` |
| Tray balloons: Script Stopped, Script Error, Relogin | macOS notifications with the same titles, while the main window is closed or minimised or the app isn't frontmost | works | `ScriptStatusAlertsTests.With_the_main_window_closed_a_stop_an_error_and_a_relogin_each_post_a_notification`, `ScriptStatusAlertsTests.While_the_main_window_is_in_front_nothing_is_posted_but_minimised_or_behind_another_app_it_is`. The error's notification carries the Script's error message, and the stop that follows an error isn't posted again. |
| Top Most (the window's system menu) | Window › Top Most, per window | works | `AppMenuTests.Top_Most_toggles_from_each_windows_Window_menu_and_persists`. Top Most is saved on macOS (deliberate); Windows only toggles it. |

The Mac App's main window also has a login bar, a status strip, the Question sheet and the Notices list, which Windows lacks (#77, #80).

## Main menu (`MainMenuViewModel`, `MainMenuUserControl.xaml`)

| Item | View model | Status | Evidence or reason |
|---|---|---|---|
| **+** (Bot Window) | `BotWindowViewModel` | works | `BotWindowTests.The_plus_button_and_the_menu_bar_item_open_the_Bot_Window_listing_every_panel_and_showing_the_selected_one`, `BotWindowTests.Jump_inside_the_Bot_Window_moves_the_player`. The **+** sits after Auto and Jump; the menu bar has it as Window › Bot Window. |
| Jump | `JumpViewModel` | works | `HelpersTests.Picking_a_cell_in_Jump_moves_the_player_there` |
| Auto | `AutoViewModel` | works | `HelpersTests.Auto_attack_starts_and_stops_from_the_Auto_view_and_the_bar_marks_it_running` |
| Scripts | `ScriptLoaderViewModel` | works | `ScriptsPanelTests.A_Script_picked_from_the_Script_Source_starts_logs_live_and_stops_from_the_panel`, `ScriptsPanelTests.A_Script_started_with_the_CLI_shows_running_and_stopping_it_from_the_panel_ends_script_wait` |
| Options › Game | `GameOptionsViewModel` | works | `OptionsTests.A_game_option_changed_in_the_panel_reaches_the_game_at_once_and_is_saved_for_the_next_start` |
| Options › Application | `ApplicationOptionsViewModel` | works | `OptionsTests.Application_Options_leaves_out_Clear_Flash_Cache_and_the_frame_rate_and_every_other_option_shows_and_is_saved_for_the_next_start`, `PanelTests.Each_view_binds_to_its_view_model_with_no_binding_errors`. Each option saves to the settings through Core's own command. Its start-up check options drive the start-up checks (`StartUpChecksTests.*`), and Show Username in Title the main window's title (`MainWindowTitleTests`, under Main window). Clear Flash Cache and Client Animation Frame-rate are hidden (deliberate): the Game Host runs Ruffle, so there is no Flash cache, and the frame-rate sets WPF's `Timeline.DesiredFrameRate`, which Avalonia has no counterpart for. The Mac App's view leaves them out; Core's list, and so the Windows app, keeps them. |
| Options › CoreBots | `CoreBotsViewModel` | works | `ScriptOptionsTests.CoreBots_options_load_from_the_players_file_and_edits_persist_there` |
| Options › Application Themes | `ApplicationThemesViewModel` | works | `OptionsTests.Switching_the_theme_restyles_every_open_window_and_is_kept`, `OptionsTests.A_theme_edited_and_saved_in_the_panel_is_listed_kept_and_removable` |
| Options › HotKeys | `HotKeysViewModel` | works | `HotKeysTests.A_hotkey_assigned_in_the_panel_is_saved_works_at_once_and_is_bound_again_after_a_restart`. Hotkeys default to ⌘ digits (deliberate), so plain keys stay with the game. |
| Helpers › Runtime | `RuntimeHelpersViewModel` | works | `HelpersTests.Runtime_adds_drops_and_quests_removes_them_by_key_and_opens_Notify_Drop` |
| Helpers › Fast Travel | `FastTravelViewModel` | works | `HelpersTests.A_Fast_Travel_entry_added_in_the_panel_joins_its_map_and_edits_in_a_dialog` |
| Helpers › Current Drops | `CurrentDropsViewModel` | works | `HelpersTests.A_drop_appears_in_Current_Drops_and_the_search_filters_it` |
| Tools › Loader | `LoaderViewModel` | works | `ToolsTests.The_Loader_loads_a_shop_by_ID_and_the_quests_picked_from_its_searched_list` |
| Tools › Grabber | `GrabberViewModel` | works | `ToolsTests.The_Grabber_lists_the_inventory_and_shows_the_selected_items_properties`. The property grid is read-only (deliberate). |
| Tools › Junk Items | `JunkItemsViewModel` | works | `ToolsTests.Junk_Items_lists_the_inventory_and_marks_the_checked_items_as_junk` |
| Tools › Stats | `ScriptStatsViewModel` | works | `ToolsTests.Stats_shows_the_Scripts_counts_as_they_change_on_other_threads` |
| Tools › Console | `ConsoleViewModel` | works | `ToolsTests.Console_runs_a_line_against_the_game` |
| Skills | `AdvancedSkillsViewModel` | works | `SkillsTests.A_skill_set_built_in_the_editor_saves_and_the_running_Script_loads_it_and_its_edits`. A pasted skill set is saved (deliberate); on Windows a paste is lost on reload. |
| Packets › Spammer | `PacketSpammerViewModel` | works | `PacketsTests.The_Spammer_sends_a_packet_to_the_server_or_the_game_and_spams_its_list_until_stopped` |
| Packets › Logger | `PacketLoggerViewModel` | works | `PacketsTests.The_Logger_lists_the_games_packets_as_they_come_searched_and_without_the_unchecked_filters` |
| Packets › Interceptor | `PacketInterceptorViewModel` | works | `PacketsTests.The_Interceptor_relays_the_game_through_its_proxy_and_lists_each_packet_by_direction`, `PacketsTests.Connect_waits_for_a_slow_server_to_let_the_game_into_the_world_even_while_another_Script_is_stopping`, `PacketsTests.Connect_that_doesnt_get_the_game_into_the_world_stops_the_proxy_and_says_so_and_the_next_Connect_clears_it`. Connect ends once the game is in the world, or after the Login Timeout with the proxy stopped; the Mac App shows why under the button (#127). |
| Bank | `IScriptBank.Open` | works | `ToolsTests.Bank_in_the_main_menu_opens_the_games_bank_panel` |
| Logs | `LogsViewModel`, `LogTabViewModel` | works | `PanelTests.The_Logs_panel_shows_Script_debug_and_flash_lines_live_and_redacted` |
| Plugins › View Plugins | `PluginsViewModel` | works | `ToolsTests.Plugins_load_from_the_data_folder_and_one_that_needs_WPF_is_logged_not_fatal`. A plugin that needs WPF fails with a log line (deliberate). |
| Plugins › a plugin's own items | `AddPluginMenuItemMessage` | works | same test: a plugin's items appear in both menus, and unloading the plugin removes them |

Every item above that opens a window also opens it through the window service in `ViewCoverageTests.Every_window_the_Windows_app_registers_opens_in_the_Mac_App_with_its_view`.

## Other windows and panels

| Windows | View model | Status | Evidence or reason |
|---|---|---|---|
| Script Repo (Scripts › Search Scripts) | `ScriptRepoViewModel` | works | `ScriptsPanelTests.A_Script_picked_from_the_Script_Source_starts_logs_live_and_stops_from_the_panel`. Update runs the Engine's `scripts_update`, which covers Update All and Download All. Open in VSCode for one Script goes through `MacProcessService`: `ScriptsPanelTests.Open_in_VSCode_on_a_Scripts_context_menu_goes_through_the_apps_process_service`. Refresh asked for while one runs restarts it, and a failed `scripts.json` fetch ends at once on a 404 and within the client's 60 s timeout otherwise, retries included: `ScriptRepoRefreshTests`. |
| Notify Drop (Runtime › Notify…) | `NotifyDropViewModel` | works | `HelpersTests.Runtime_adds_drops_and_quests_removes_them_by_key_and_opens_Notify_Drop` |
| Registered quests and pickup drops (Runtime's parts) | `RegisteredQuestsViewModel`, `ToPickupDropsViewModel` | works | `HelpersTests.Runtime_adds_drops_and_quests_removes_them_by_key_and_opens_Notify_Drop` |
| Boosts (Runtime's part) | `BoostsViewModel` | present | `PanelTests.Each_view_binds_to_its_view_model_with_no_binding_errors`. Using a boost needs boost items in the inventory, which no test sets up. |
| Fast Travel editor | `FastTravelEditorViewModel`, `FastTravelItemViewModel` | works | `HelpersTests.A_Fast_Travel_entry_added_in_the_panel_joins_its_map_and_edits_in_a_dialog` |
| Grabber lists and tasks | `GrabberListViewModel`, `GrabberTaskViewModel` | works | `ToolsTests.The_Grabber_lists_the_inventory_and_shows_the_selected_items_properties` grabs and lists. No test runs a task (such as selling or banking an item). |
| Skill editor, saved sets, rules | `AdvancedSkillEditorViewModel`, `SavedAdvancedSkillsViewModel`, `SkillRulesViewModel` | works | `SkillsTests.A_saved_skill_set_copies_to_the_clipboard_and_pastes_back`, `SkillsTests.A_skill_set_built_in_the_editor_saves_and_the_running_Script_loads_it_and_its_edits` |
| CoreBots tabs: options, other options | `CBOptionsViewModel`, `CBOOtherOptionsViewModel` and the option rows | works | `ScriptOptionsTests.CoreBots_options_load_from_the_players_file_and_edits_persist_there` |
| CoreBots tab: loadout, class equipment and class select | `CBOLoadoutViewModel`, `CBOClassEquipmentViewModel`, `CBOClassSelectViewModel` | present | `PanelTests.Each_view_binds_to_its_view_model_with_no_binding_errors`. No test edits a loadout. |
| Theme settings and colour scheme editor | `ThemeSettingsViewModel`, `ColorSchemeEditorViewModel`, `BackgroundThemeViewModel` | works | `OptionsTests.A_theme_edited_and_saved_in_the_panel_is_listed_kept_and_removable`, `OptionsTests.Open_Themes_Folder_and_Open_VSCode_go_through_the_apps_process_service` |
| Hotkey rows | `HotKeyItemViewModel` | works | `HotKeysTests.A_hotkey_assigned_in_the_panel_is_saved_works_at_once_and_is_bound_again_after_a_restart` |
| Plugin rows | `PluginItemViewModel` | works | `ToolsTests.Plugins_load_from_the_data_folder_and_one_that_needs_WPF_is_logged_not_fatal` |
| Option rows (`OptionItemUserControl`) | `DisplayOptionItemViewModelBase` and its subtypes | works | `OptionsTests.A_game_option_changed_in_the_panel_reaches_the_game_at_once_and_is_saved_for_the_next_start` |
| About | `AboutViewModel` | works | In the app menu. `AppMenuTests.About_and_Change_Logs_open_once_from_the_app_menu_and_show_their_pages` |
| Change Logs | `ChangeLogsViewModel` | works | In the app menu. Same test. |
| GitHub login | `GitHubAuthViewModel` | works | In the app menu. `AppMenuTests.GitHub_login_completes_against_the_fake_device_flow_and_the_token_lands_in_Keychain_only`. The token is kept in Keychain (deliberate). |
| Bot Window | `BotWindowViewModel` | works | `BotWindowTests.Search_filters_by_title_and_Home_Previous_and_Next_move_through_every_panel_with_no_binding_errors`, `BotWindowTests.A_panel_stays_active_while_its_own_window_or_the_Bot_Window_shows_it_and_stops_once_neither_does`. The panel list is always shown, not in a drawer, and the search ignores case (deliberate: as the Mac App's other searches). A panel shown in its own window and the Bot Window stays active until neither shows it; on Windows either one deactivates it. |
| `HostWindow` (managed windows) | any | works | `ViewCoverageTests.Every_window_the_Windows_app_registers_opens_in_the_Mac_App_with_its_view`, `PanelTests.The_Scripts_menu_item_opens_the_Scripts_window_once` |
| `CustomWindow`: title bar, minimise, maximise, close | The native macOS title bar | deliberate | macOS draws its own window frame. |
| `PropertyGrid` | Read-only `PropertyGrid` | deliberate | Editing a grabbed item's snapshot on Windows changes nothing in the game (#84). |
| `BalloonTipUserControl` | macOS notifications | works | For Script Dialogs, and for a Script's stop and error and a relogin. `ScriptStatusAlertsTests.With_the_main_window_closed_a_stop_an_error_and_a_relogin_each_post_a_notification` |

## Dialogs

| Windows | View model | Status | Evidence or reason |
|---|---|---|---|
| Message box (`MessageBoxDialog`) | `MessageBoxDialogViewModel` | works | `GenericDialogTests.A_message_box_answers_OK_Yes_or_No_and_closing_it_answers_nothing` |
| Custom message box (`CustomMessageBoxDialog`) | `CustomDialogViewModel` | works | `GenericDialogTests.A_custom_dialog_answers_the_button_clicked_and_closing_it_leaves_no_answer` |
| Input dialog | `InputDialogViewModel` | works | `GenericDialogTests.The_input_dialog_cancels_with_false` |
| A Script's message boxes (`IDialogService`) | Questions on a sheet, Notices in a list, through the one broker | works | `ScriptDialogTests.A_Question_shows_a_sheet_and_answering_it_there_resumes_the_Script_as_answered_by_user`, `ScriptDialogTests.Notices_show_in_the_list_with_a_badge_and_never_stall_the_Script_even_on_the_timer_thread` |
| A Script's `ShowDialog(vm)` | `DialogWindow` | works | `ScriptDialogTests.ShowDialog_from_a_Scripts_thread_shows_a_real_dialog_that_waits_for_its_view_to_close_it` |
| Script options (`OptionContainerUserControl`) | `OptionContainerViewModel` | works | `ScriptOptionsTests.A_Script_with_options_opens_the_editor_and_what_is_saved_there_is_what_skua_script_options_shows_and_the_next_run_uses` |
| Fast Travel editor dialog | `FastTravelEditorDialogViewModel` | works | `HelpersTests.A_Fast_Travel_entry_added_in_the_panel_joins_its_map_and_edits_in_a_dialog` |
| Assign hotkey | `AssignHotKeyDialogViewModel` | works | `HotKeysTests.The_assign_dialog_keeps_its_key_on_Esc_and_refuses_a_modifier_alone_Control_or_saving_while_it_waits` |
| Skill rule editor | `SkillRuleEditorDialogViewModel` | works | `SkillsTests.Return_on_a_skill_edits_its_rules_in_a_dialog_and_Confirm_gives_the_skill_the_edit` |
| Select group (Manager) | `SelectGroupDialogViewModel` | works | `ManagerTests.Tags_filter_the_list_and_a_group_launches_its_accounts_as_on_Windows` |
| Open and save file dialogs (`IFileDialogService`) | `AvaloniaFileDialogService` (native panels) | present | Used by Load Script, Script… and Import. No test drives a native panel. |

## Application start and command line (`App.xaml.cs`, `SkuaStartupHandler`)

| Windows | Mac App | Status | Evidence or reason |
|---|---|---|---|
| `-u`/`-p`: log in with a username and password | `--account <name>`: the account's password is read from Keychain | deliberate | Passwords are kept in Keychain, never on the command line. `ManagerTests.The_app_command_line_round_trips_and_never_takes_a_password` |
| `-s`: the server | `--server` | works | `StatusTests.A_launch_from_the_Skua_Manager_logs_in_on_its_server_then_starts_its_Script`, `ManagerTests.The_app_command_line_round_trips_and_never_takes_a_password` |
| `--run-script` | `--script` | works | `StatusTests.A_launch_from_the_Skua_Manager_logs_in_on_its_server_then_starts_its_Script`, `ManagerTests.The_app_command_line_round_trips_and_never_takes_a_password` |
| `--use-theme` | None | deliberate | The Manager's theme sync is part of the Windows Manager's Options, which macOS doesn't have. The theme lives in the data folder's settings, which every app reads. |
| `--gh-token` | None | deliberate | The GitHub token is kept in Keychain. |
| One client per process, many at once | `--name`: one app per Engine Name | works | `AppEngineTests.Skua_status_connects_to_the_app_hosted_Engine_and_says_the_app_hosts_it`, `TakeOverTests.*`. Remembering the last Engine Name is part of [#88](https://github.com/noelrohi/Skua/issues/88) (in progress). |
| Flash trust file, Flash cache | None | deliberate | The Game Host runs Ruffle, so there is no Flash cache. |
| Start-up checks: Scripts, AdvanceSkill sets, junk items, quest data | The same, once the main window shows, as Application Options say | works | `StartUpChecksTests.*` against the fake Script Source. The Scripts update through the Engine's `scripts_update` instead of `DownloadAllWhereAsync`, so it is refused while a Script runs (the check is skipped, with a `debug` line) and recorded in the Script history. Questions show as the sheet and Notices in Notices. `skua-engine` runs none of them: `ScriptSourceTests.A_headless_Engine_runs_none_of_the_Mac_Apps_start_up_checks`. |
| Plugins load at start | The same | works | `ToolsTests.Plugins_load_from_the_data_folder_and_one_that_needs_WPF_is_logged_not_fatal` |
| HotKeys load at start | The same | works | `HotKeysTests.A_hotkey_assigned_in_the_panel_is_saved_works_at_once_and_is_bound_again_after_a_restart` |
| The server list is fetched at start | The login bar's server picker | works | `StatusTests.Log_in_reaches_logged_in_with_the_account_map_and_server_shown_and_Log_out_returns_to_the_login_screen` |
| Client animation frame rate (`AnimationFrameRate`) | None | deliberate | WPF's `Timeline` setting has no Avalonia counterpart. [#114](https://github.com/noelrohi/Skua/issues/114) hides the option. |
| Exit: stop the Script, the capture proxy and the Game Client | Quitting stops the Engine | works | `CloseAndQuitTests.A_quit_without_asking_as_on_SIGTERM_never_asks_and_ends_a_question_on_screen` |
| Installer (`Skua.Installer`) | `Skua.app` and `install-macos.sh --app` | in progress [#88](https://github.com/noelrohi/Skua/issues/88) | |

## Skua Manager (`Skua.Manager`)

| Windows | Mac App (`Skua --manager`) | Status | Evidence or reason |
|---|---|---|---|
| Accounts tab | `AccountManagerViewModel`, through `ManagerAccountsViewModel` | works | `ManagerTests.Adding_editing_and_removing_an_account_keeps_its_password_only_in_Keychain`. Passwords are kept in Keychain (deliberate). |
| Accounts: groups and tags | `GroupItemViewModel`, `TagFilterItem`, the group picker | works | `ManagerTests.Tags_filter_the_list_and_a_group_launches_its_accounts_as_on_Windows` |
| Accounts: Start, Start with Script, Start all or the selected | Launch, With Script, Launch selected, Launch all shown | works | `ManagerTests.Launching_two_accounts_starts_an_app_each_that_plays_it_and_stopping_one_leaves_the_other_playing`. The launched app's side of a server and a Script: `StatusTests.A_launch_from_the_Skua_Manager_logs_in_on_its_server_then_starts_its_Script` |
| Accounts: import a Windows list | Import Windows list… | works | `ManagerTests.Importing_a_Windows_list_moves_its_passwords_to_Keychain_and_keeps_a_backup`. This exists only on macOS. |
| Accounts: Get Scripts (opens the Script Repo) | None | deliberate | The Manager hosts no Engine (ADR 0006), and Scripts change only through an Engine's `scripts_update`, which refuses while a Script runs. Update the Scripts from an app's Script Repo, or with `skua scripts update`. |
| Launcher tab | Running tab | deliberate | Running replaces the Launcher (ADR 0006). `ManagerTests.Launching_two_accounts_starts_an_app_each_that_plays_it_and_stopping_one_leaves_the_other_playing` |
| Updates tab (`ClientUpdatesViewModel`) | Updates tab | deliberate | Updates replaces Client Updates and downloads nothing (ADR 0006). `ManagerTests.The_updates_view_compares_the_installed_build_with_the_checkout_and_downloads_nothing` |
| Options tab (`ManagerOptionsViewModel`) | None | deliberate | No macOS counterpart for the download folder or theme sync. The GitHub token is kept in Keychain (ADR 0006). |
| Themes tab | Each app's Options › Application Themes | deliberate | The theme is in the data folder's settings, which every app reads. The Manager window keeps the default dark theme. |
| Goals tab | `GoalsViewModel` | present | `ManagerTests.Each_Manager_view_binds_to_its_view_model_with_no_binding_errors`. Goals is self-contained and needs no game, so no test drives it. |
| About and Change Logs tabs | Each app's app menu | works | `AppMenuTests.About_and_Change_Logs_open_once_from_the_app_menu_and_show_their_pages` |
| Change Logs on the first start | Each app's first start, with the same `ChangeLogActivated` setting | works | `StartUpChecksTests.Change_Logs_opens_on_the_first_start_and_not_on_later_ones` |
| Tray: Show Manager, single instance | The Skua Manager… item in every app's menu bar and Dock menu, which brings the running Manager to the front | present | `ManagerTests.The_app_menu_bar_and_window_menu_open_the_Manager` checks the items. The single instance (`manager.lock`) and bringing it to the front were checked by hand in #93's smoke run; no test starts a second Manager process. |
| Tray: Skua Bot AQW (start a new client) | Accounts › Launch | deliberate | Running and Accounts replace the Launcher (ADR 0006). A blank app starts from `Skua.app` ([#88](https://github.com/noelrohi/Skua/issues/88), in progress). |
| Tray: Update Scripts | An app's Script Repo › Update, or `skua scripts update` | works | `ScriptsPanelTests.A_Script_picked_from_the_Script_Source_starts_logs_live_and_stops_from_the_panel` |
| Tray: Reset Scripts | An app's Script Repo › Reset…, which asks first; the Engine refuses it while a Script runs or an update is in flight, as `scripts_update`. It keeps the junk items list, which Windows deletes. | works | `ScriptsPanelTests.Reset_asks_first_then_leaves_the_Scripts_folder_matching_the_Script_Source_with_a_local_edit_gone`, `ScriptsPanelTests.Reset_is_refused_with_a_message_while_a_Script_runs_and_deletes_nothing`, `ScriptsPanelTests.Reset_is_refused_as_busy_while_a_Scripts_update_is_in_flight`. Only the Mac App resets; no Control Surface can, so the protocol is unchanged. |
| Tray: Check Client Update | Updates tab | deliberate | As for the Updates tab. |
| Tray: Exit, which kills every Skua client | Quitting the Manager leaves its apps playing. Stop each app from Running. | deliberate | Each app owns its Engine, and the apps outlive the Manager (ADR 0006). |

## Out of scope

`Skua.App.WPF.Lite`, `Skua.App.WPF.Follower`, `Skua.App.WPF.Sync` and `Skua.SyncConsole` are separate Windows tools, outside #61.

The live check with the Game View open is [#89](https://github.com/noelrohi/Skua/issues/89), which is for a human.
