define(['baseView', 'loading', 'toast', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-select'], function (BaseView, loading, toast) {
    'use strict';

    const pluginId = '4e3a9c85-90c7-4f32-8591-7a713c33a901';
    const defaultConfig = {
        LibraryIds: [],
        ReplaceNativeIntroMarkers: false,
        ReplaceExistingCreditsMarkers: true,
        EnableIntroDetection: true,
        EnableCreditsDetection: true,
        MaxParallelTasks: 1,
        EnableLibraryScanOnImport: true,
        EnableCache: true,
        EnableTheIntroDb: true,
        TheIntroDbApiKey: '',
        TheIntroDbMinConfidence: 0.80,
        TheIntroDbMinSubmissions: 2,
        DetectionStrategy: 'Auto',
        MaxSampleEpisodes: 6,
        IntroAnalysisSeconds: 240,
        CreditsAnalysisSeconds: 300,
        MinIntroDurationSeconds: 15,
        MaxIntroDurationSeconds: 150,
        MinCreditsDurationSeconds: 20,
        MaxCreditsDurationSeconds: 240,
        SampleFps: 1,
        MatchThreshold: 0.88,
        MatchWindowSeconds: 12,
        MinEpisodesPerSeason: 2,
        ImportDebounceSeconds: 90
    };

    function normalizeConfig(config) {
        const merged = Object.assign({}, defaultConfig, config || {});
        merged.LibraryIds = Array.isArray(merged.LibraryIds) ? merged.LibraryIds.filter(Boolean) : [];
        return merged;
    }

    function setChecked(view, selector, value) {
        const el = view.querySelector(selector);
        if (el) {
            el.checked = !!value;
        }
    }

    function getChecked(view, selector) {
        const el = view.querySelector(selector);
        return !!(el && el.checked);
    }

    function setValue(view, selector, value) {
        const el = view.querySelector(selector);
        if (el) {
            el.value = value;
        }
    }

    function getNumber(view, selector, fallback) {
        const el = view.querySelector(selector);
        const val = Number.parseFloat(el.value);
        return Number.isFinite(val) ? val : fallback;
    }

    function setText(view, selector, value) {
        const el = view.querySelector(selector);
        if (el) {
            el.textContent = value;
        }
    }

    function renderLibraries(view, mediaFolders, selectedIds) {
        const select = view.querySelector('#selectLibraries');
        const selectedSet = new Set((selectedIds || []).map(function (id) {
            return String(id);
        }));
        const libraries = (mediaFolders.Items || []).filter(function (lib) {
            return lib.CollectionType === 'tvshows' || lib.CollectionType === 'mixed' || !lib.CollectionType;
        });

        if (!libraries.length) {
            select.innerHTML = '';
            select.disabled = true;
            if (select.setValues) {
                select.setValues([], false, []);
            }
            return;
        }

        select.disabled = false;
        select.innerHTML = '';

        libraries.sort(function (a, b) {
            return String(a.Name || '').localeCompare(String(b.Name || ''), 'zh-Hans-CN');
        });

        libraries.forEach(function (lib) {
            const id = String(lib.Id || lib.Guid || '');
            const option = document.createElement('option');
            option.value = id;
            option.textContent = lib.Name || id;
            option.selected = selectedSet.has(id);
            select.appendChild(option);
        });

        const selectedValues = libraries
            .map(function (lib) {
                return String(lib.Id || lib.Guid || '');
            })
            .filter(function (id) {
                return selectedSet.has(id);
            });

        if (select.setValues) {
            select.setValues(selectedValues, false);
        }
    }

    function loadStatus(view) {
        return ApiClient.getJSON(ApiClient.getUrl('IntroMarker/Status')).then(function (status) {
            const runtime = status.Runtime || status || {};
            const isRunning = !!runtime.IsRunning;
            const badge = view.querySelector('#statusBadge');

            setText(view, '#statusTime', runtime.LastRunAt ? '更新时间：' + runtime.LastRunAt : '尚未运行');
            setText(view, '#statusSeasons', (runtime.SeasonsCompleted || 0) + ' / ' + (runtime.SeasonsTotal || 0));
            setText(view, '#statusEpisodes', String(runtime.EpisodesScanned || 0));
            setText(view, '#statusStage', runtime.CurrentStage || '-');
            setText(view, '#statusMessage', runtime.LastMessage || '-');
            setText(view, '#pluginVersion', status.Version ? '版本：' + status.Version : '版本：-');

            const repoLink = view.querySelector('#repoLink');
            if (repoLink && status.RepositoryUrl) {
                repoLink.href = status.RepositoryUrl;
                repoLink.textContent = status.RepositoryUrl;
            }

            if (badge) {
                badge.textContent = isRunning ? '运行中' : '空闲';
                badge.style.background = isRunning ? 'rgba(46, 204, 113, .18)' : 'rgba(128, 128, 128, .18)';
                badge.style.color = isRunning ? '#2ecc71' : 'inherit';
            }
        });
    }

    function switchTab(view, target) {
        const navButtons = view.querySelectorAll('.nav-button');
        const pages = view.querySelectorAll('.configTabPage');

        navButtons.forEach(function (btn) {
            btn.classList.toggle('ui-btn-active', btn.getAttribute('data-target') === target);
        });

        pages.forEach(function (page) {
            page.classList.toggle('hide', page.id !== target);
        });

        localStorage.setItem('introMarker_activeTab', target);
    }

    function save(view, config) {
        const nextConfig = normalizeConfig(config);
        nextConfig.ReplaceNativeIntroMarkers = getChecked(view, '#chkReplaceNativeIntroMarkers');
        nextConfig.ReplaceExistingCreditsMarkers = getChecked(view, '#chkReplaceExistingCreditsMarkers');
        nextConfig.EnableIntroDetection = getChecked(view, '#chkEnableIntroDetection');
        nextConfig.EnableCreditsDetection = getChecked(view, '#chkEnableCreditsDetection');
        nextConfig.EnableLibraryScanOnImport = getChecked(view, '#chkEnableLibraryScanOnImport');
        nextConfig.EnableCache = getChecked(view, '#chkEnableCache');
        nextConfig.EnableTheIntroDb = getChecked(view, '#chkEnableTheIntroDb');
        nextConfig.TheIntroDbApiKey = (view.querySelector('#txtTheIntroDbApiKey')?.value || '').trim();
        nextConfig.TheIntroDbMinConfidence = getNumber(view, '#txtTheIntroDbMinConfidence', defaultConfig.TheIntroDbMinConfidence);
        nextConfig.TheIntroDbMinSubmissions = getNumber(view, '#txtTheIntroDbMinSubmissions', defaultConfig.TheIntroDbMinSubmissions);
        nextConfig.DetectionStrategy = view.querySelector('#selectDetectionStrategy')?.value || defaultConfig.DetectionStrategy;
        nextConfig.MaxSampleEpisodes = getNumber(view, '#txtMaxSampleEpisodes', defaultConfig.MaxSampleEpisodes);
        nextConfig.MaxParallelTasks = getNumber(view, '#txtMaxParallelTasks', defaultConfig.MaxParallelTasks);
        nextConfig.ImportDebounceSeconds = getNumber(view, '#txtImportDebounceSeconds', defaultConfig.ImportDebounceSeconds);
        nextConfig.IntroAnalysisSeconds = getNumber(view, '#txtIntroAnalysisSeconds', defaultConfig.IntroAnalysisSeconds);
        nextConfig.CreditsAnalysisSeconds = getNumber(view, '#txtCreditsAnalysisSeconds', defaultConfig.CreditsAnalysisSeconds);
        nextConfig.MinIntroDurationSeconds = getNumber(view, '#txtMinIntroDurationSeconds', defaultConfig.MinIntroDurationSeconds);
        nextConfig.MaxIntroDurationSeconds = getNumber(view, '#txtMaxIntroDurationSeconds', defaultConfig.MaxIntroDurationSeconds);
        nextConfig.MinCreditsDurationSeconds = getNumber(view, '#txtMinCreditsDurationSeconds', defaultConfig.MinCreditsDurationSeconds);
        nextConfig.MaxCreditsDurationSeconds = getNumber(view, '#txtMaxCreditsDurationSeconds', defaultConfig.MaxCreditsDurationSeconds);
        nextConfig.SampleFps = getNumber(view, '#txtSampleFps', defaultConfig.SampleFps);
        nextConfig.MatchThreshold = getNumber(view, '#txtMatchThreshold', defaultConfig.MatchThreshold);
        nextConfig.MatchWindowSeconds = getNumber(view, '#txtMatchWindowSeconds', defaultConfig.MatchWindowSeconds);
        nextConfig.MinEpisodesPerSeason = getNumber(view, '#txtMinEpisodesPerSeason', defaultConfig.MinEpisodesPerSeason);
        const librariesSelect = view.querySelector('#selectLibraries');
        nextConfig.LibraryIds = librariesSelect && librariesSelect.getValues
            ? librariesSelect.getValues()
            : Array.from((librariesSelect && librariesSelect.selectedOptions) || []).map(function (el) {
                return el.value;
            });

        loading.show();
        return ApiClient.updatePluginConfiguration(pluginId, nextConfig).then(function () {
            toast('配置已保存');
            return nextConfig;
        }).finally(function () {
            loading.hide();
        });
    }

    return class extends BaseView {
        constructor(view, params) {
            super(view, params);
            this.config = null;
        }

        bindOnce(view) {
            if (view.dataset.introMarkerBound) {
                return;
            }
            view.dataset.introMarkerBound = '1';

            const form = view.querySelector('.introMarkerForm');
            form.addEventListener('submit', (e) => {
                e.preventDefault();
                save(view, this.config || {}).then((config) => {
                    this.config = config;
                });
                return false;
            });

            view.querySelectorAll('.nav-button').forEach((btn) => {
                btn.addEventListener('click', (e) => {
                    e.preventDefault();
                    switchTab(view, btn.getAttribute('data-target'));
                });
            });

            view.querySelector('#btnScanNow').addEventListener('click', () => {
                loading.show();
                ApiClient.ajax({
                    type: 'POST',
                    url: ApiClient.getUrl('IntroMarker/ScanNow')
                }).then(function (result) {
                    toast(result.Message || '已提交');
                    return loadStatus(view);
                }).finally(function () {
                    loading.hide();
                });
            });

            view.querySelector('#btnClearCache').addEventListener('click', () => {
                loading.show();
                ApiClient.ajax({
                    type: 'POST',
                    url: ApiClient.getUrl('IntroMarker/ClearCache')
                }).then(function (result) {
                    toast(result.Message || '缓存已清空');
                }).finally(function () {
                    loading.hide();
                });
            });

            view.querySelector('#btnRefreshStatus').addEventListener('click', () => {
                loadStatus(view);
            });
        }

        onResume(options) {
            super.onResume(options);
            const view = this.view;

            this.bindOnce(view);
            loading.show();
            Promise.all([
                ApiClient.getPluginConfiguration(pluginId),
                ApiClient.getJSON(ApiClient.getUrl('Library/MediaFolders'))
            ]).then(results => {
                this.config = normalizeConfig(results[0]);
                const folders = results[1] || { Items: [] };
                renderLibraries(view, folders, this.config.LibraryIds || []);
                setChecked(view, '#chkEnableTheIntroDb', this.config.EnableTheIntroDb);
                setValue(view, '#txtTheIntroDbApiKey', this.config.TheIntroDbApiKey || '');
                setChecked(view, '#chkReplaceNativeIntroMarkers', this.config.ReplaceNativeIntroMarkers);
                setChecked(view, '#chkReplaceExistingCreditsMarkers', this.config.ReplaceExistingCreditsMarkers);
                setChecked(view, '#chkEnableIntroDetection', this.config.EnableIntroDetection);
                setChecked(view, '#chkEnableCreditsDetection', this.config.EnableCreditsDetection);
                setChecked(view, '#chkEnableLibraryScanOnImport', this.config.EnableLibraryScanOnImport);
                setChecked(view, '#chkEnableCache', this.config.EnableCache);
                setValue(view, '#txtTheIntroDbMinConfidence', this.config.TheIntroDbMinConfidence);
                setValue(view, '#txtTheIntroDbMinSubmissions', this.config.TheIntroDbMinSubmissions);
                setValue(view, '#selectDetectionStrategy', this.config.DetectionStrategy || defaultConfig.DetectionStrategy);
                setValue(view, '#txtMaxSampleEpisodes', this.config.MaxSampleEpisodes);
                setValue(view, '#txtMaxParallelTasks', this.config.MaxParallelTasks);
                setValue(view, '#txtImportDebounceSeconds', this.config.ImportDebounceSeconds);
                setValue(view, '#txtIntroAnalysisSeconds', this.config.IntroAnalysisSeconds);
                setValue(view, '#txtCreditsAnalysisSeconds', this.config.CreditsAnalysisSeconds);
                setValue(view, '#txtMinIntroDurationSeconds', this.config.MinIntroDurationSeconds);
                setValue(view, '#txtMaxIntroDurationSeconds', this.config.MaxIntroDurationSeconds);
                setValue(view, '#txtMinCreditsDurationSeconds', this.config.MinCreditsDurationSeconds);
                setValue(view, '#txtMaxCreditsDurationSeconds', this.config.MaxCreditsDurationSeconds);
                setValue(view, '#txtSampleFps', this.config.SampleFps);
                setValue(view, '#txtMatchThreshold', this.config.MatchThreshold);
                setValue(view, '#txtMatchWindowSeconds', this.config.MatchWindowSeconds);
                setValue(view, '#txtMinEpisodesPerSeason', this.config.MinEpisodesPerSeason);
                switchTab(view, localStorage.getItem('introMarker_activeTab') || 'tabGeneral');
                return loadStatus(view);
            }).finally(function () {
                loading.hide();
            });
        }
    };
});
