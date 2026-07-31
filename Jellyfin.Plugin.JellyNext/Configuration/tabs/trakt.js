// Trakt tab initialization and logic

var TRAKT_AUTH_MODE = {
    STANDALONE: 0,
    SHARED_TOKEN: 1,
    SHARED_CLIENT_ID: 2
};

var TRAKT_AUTH_MODE_DESCRIPTIONS = {
    0: 'JellyNext uses its own Trakt application. Needs a free app slot of its own, so this will '
        + 'fail if the official Trakt plugin already occupies the single slot a free Trakt account gets.',
    1: 'JellyNext presents the Trakt plugin\'s client ID and borrows its stored access token per user. '
        + 'No second authorization happens, so Trakt only ever sees one connected app. Requires the '
        + 'official Trakt plugin to be installed and each user linked there.',
    2: 'JellyNext presents the Trakt plugin\'s client ID but authorizes and stores its own token. '
        + 'Experimental: it is unverified whether Trakt keeps the Trakt plugin\'s earlier token valid '
        + 'when the same client ID is authorized a second time. If it does not, the two plugins will '
        + 'repeatedly knock each other offline.'
};

function initTraktTab() {
    // Set up event listeners for Trakt tab
    setupTraktEventListeners();
    onNextSeasonsRecentOnlyChanged();
    onNotifyNewSeasonsChanged();
    console.log('Trakt tab initialized');
}

function getSelectedAuthMode() {
    var selector = document.getElementById('TraktAuthMode');
    return selector ? parseInt(selector.value, 10) : TRAKT_AUTH_MODE.STANDALONE;
}

function loadTraktSettings(config) {
    document.getElementById('TraktAuthMode').value = config.TraktAuthMode || 0;
    document.getElementById('AllowSharedTokenRefresh').checked = config.AllowSharedTokenRefresh !== false;
    onAuthModeChanged();
}

function saveTraktSettings(config) {
    config.TraktAuthMode = getSelectedAuthMode();
    config.AllowSharedTokenRefresh = document.getElementById('AllowSharedTokenRefresh').checked;
}

// Reflect the selected mode in the surrounding UI. The mode itself only takes effect once the
// configuration is saved, so the status text describes the saved state, not the pending selection.
function onAuthModeChanged() {
    var mode = getSelectedAuthMode();

    document.getElementById('TraktAuthModeDescription').textContent = TRAKT_AUTH_MODE_DESCRIPTIONS[mode] || '';
    document.getElementById('AllowSharedTokenRefreshContainer').style.display =
        mode === TRAKT_AUTH_MODE.SHARED_TOKEN ? 'block' : 'none';

    loadTraktPluginStatus();

    if (JellyNextConfig.currentUserGuid) {
        checkAuthorizationStatus(JellyNextConfig.currentUserGuid);
    }
}

function onNextSeasonsRecentOnlyChanged() {
    var enabled = document.getElementById('UserNextSeasonsRecentOnly').checked;
    document.getElementById('UserNextSeasonsRecentDaysContainer').style.display = enabled ? 'block' : 'none';
}

function onNotifyNewSeasonsChanged() {
    var enabled = document.getElementById('UserNotifyNewSeasonsByEmail').checked;
    document.getElementById('UserNotificationEmailContainer').style.display = enabled ? 'block' : 'none';
}

function loadTraktPluginStatus() {
    var container = document.getElementById('TraktPluginStatus');
    var text = document.getElementById('TraktPluginStatusText');
    if (!container || !text) {
        return;
    }

    ApiClient.fetch({
        type: 'GET',
        url: ApiClient.getUrl('JellyNext/Trakt/SharedStatus')
    }).then(function (response) {
        return response.json();
    }).then(function (status) {
        JellyNextConfig.traktPluginStatus = status;
        container.style.display = 'block';

        if (!status.traktPluginAvailable) {
            text.style.color = '#cc3333';
            text.textContent = 'Official Trakt plugin: not installed or disabled. Shared modes are unavailable.';
            return;
        }

        var linkedCount = (status.linkedUserIds || []).length;
        text.style.color = '#52B54B';
        text.textContent = 'Official Trakt plugin ' + (status.traktPluginVersion || '')
            + ' detected, with ' + linkedCount + ' linked Trakt account(s).';
    }).catch(function (error) {
        console.error('Error loading Trakt plugin status:', error);
        container.style.display = 'none';
    });
}

function setupTraktEventListeners() {
    // Authorization mode change
    document.getElementById('TraktAuthMode').addEventListener('change', onAuthModeChanged);

    // The new-release window only means anything while the filter is on
    document.getElementById('UserNextSeasonsRecentOnly')
        .addEventListener('change', onNextSeasonsRecentOnlyChanged);

    // The address is only meaningful while notifications are on
    document.getElementById('UserNotifyNewSeasonsByEmail')
        .addEventListener('change', onNotifyNewSeasonsChanged);

    // Register a user whose token already lives in the official Trakt plugin
    document.getElementById('LinkSharedBtn').addEventListener('click', function () {
        if (!JellyNextConfig.currentUserGuid) {
            Dashboard.alert('Please select a user first');
            return;
        }

        Dashboard.showLoadingMsg();

        ApiClient.fetch({
            type: 'POST',
            url: ApiClient.getUrl('JellyNext/Trakt/Users/' + JellyNextConfig.currentUserGuid + '/Link')
        }).then(function (response) {
            return response.json().then(function (result) {
                if (!response.ok) {
                    throw new Error(result.error || response.statusText);
                }
                return result;
            });
        }).then(function () {
            Dashboard.hideLoadingMsg();
            Dashboard.alert('This user now uses the Trakt plugin\'s linked account.');
            checkAuthorizationStatus(JellyNextConfig.currentUserGuid);
        }).catch(function (error) {
            Dashboard.hideLoadingMsg();
            console.error('Error linking shared Trakt account:', error);
            Dashboard.alert(error.message || 'Failed to link the Trakt plugin\'s account.');
        });
    });

    // User selection change
    document.getElementById('UserSelector').addEventListener('change', function (e) {
        var userGuid = e.target.value;
        if (JellyNextConfig.authCheckInterval) {
            clearInterval(JellyNextConfig.authCheckInterval);
        }
        checkAuthorizationStatus(userGuid);
    });

    // Start OAuth authorization
    document.getElementById('AuthorizeBtn').addEventListener('click', function () {
        if (!JellyNextConfig.currentUserGuid) {
            Dashboard.alert('Please select a user first');
            return;
        }

        Dashboard.showLoadingMsg();

        ApiClient.fetch({
            type: 'POST',
            url: ApiClient.getUrl('JellyNext/Trakt/Users/' + JellyNextConfig.currentUserGuid + '/Authorize')
        }).then(function (response) {
            return response.json();
        }).then(function (result) {
            Dashboard.hideLoadingMsg();
            showAuthorizingState(result.userCode);

            // Poll for authorization completion
            JellyNextConfig.authCheckInterval = setInterval(function () {
                checkAuthorizationCompletion();
            }, 3000); // Check every 3 seconds
        }).catch(function (error) {
            Dashboard.hideLoadingMsg();
            console.error('Error starting authorization:', error);
            Dashboard.alert('Failed to start authorization. Please try again.');
        });
    });

    // Deauthorize user
    document.getElementById('DeauthorizeBtn').addEventListener('click', function () {
        if (!JellyNextConfig.currentUserGuid) {
            return;
        }

        if (!confirm('Are you sure you want to unlink this Trakt account?')) {
            return;
        }

        Dashboard.showLoadingMsg();

        ApiClient.fetch({
            type: 'POST',
            url: ApiClient.getUrl('JellyNext/Trakt/Users/' + JellyNextConfig.currentUserGuid + '/Deauthorize')
        }).then(function (response) {
            return response.json();
        }).then(function (result) {
            Dashboard.hideLoadingMsg();
            Dashboard.alert('Successfully unlinked Trakt account');
            showNotAuthorizedState();
        }).catch(function (error) {
            Dashboard.hideLoadingMsg();
            console.error('Error deauthorizing:', error);
            Dashboard.alert('Failed to unlink Trakt account');
        });
    });
}

// Check authorization status for selected user
function checkAuthorizationStatus(userGuid) {
    if (!userGuid) {
        document.getElementById('AuthorizationStatus').style.display = 'none';
        return;
    }

    JellyNextConfig.currentUserGuid = userGuid;
    document.getElementById('AuthorizationStatus').style.display = 'block';

    ApiClient.fetch({
        type: 'GET',
        url: ApiClient.getUrl('JellyNext/Trakt/Users/' + userGuid + '/AuthorizationStatus')
    }).then(function (response) {
        return response.json();
    }).then(function (status) {
        // Driven by the saved mode reported by the server, not the pending dropdown selection.
        applyLinkingMode(status);

        if (status.isAuthorized) {
            showAuthorizedState();
        } else {
            showNotAuthorizedState();
        }
    }).catch(function (error) {
        console.error('Error checking authorization status:', error);
        showNotAuthorizedState();
    });
}

// Adapt the linking controls and copy to the active authorization mode
function applyLinkingMode(status) {
    var shared = status && status.sharedMode === true;

    document.getElementById('AuthorizeBtn').style.display = shared ? 'none' : '';
    document.getElementById('LinkSharedBtn').style.display = shared ? '' : 'none';
    document.getElementById('DeauthorizeBtnLabel').textContent =
        shared ? 'Remove From JellyNext' : 'Unlink Trakt Account';
    document.getElementById('AuthorizedText').textContent =
        shared
            ? '✓ This user uses the Trakt account linked in the official Trakt plugin.'
            : '✓ This user has authorized their Trakt account.';

    var notAuthorizedText = document.getElementById('NotAuthorizedText');
    if (!shared) {
        notAuthorizedText.textContent = 'This user has not linked their Trakt account yet.';
    } else if (!status.traktPluginAvailable) {
        notAuthorizedText.textContent = 'The official Trakt plugin is not installed or not enabled, '
            + 'so there is no token to share.';
    } else if (!status.traktPluginHasToken) {
        notAuthorizedText.textContent = 'The Trakt plugin has no linked Trakt account for this user. '
            + 'Link it in the Trakt plugin\'s settings first, then come back here.';
    } else {
        notAuthorizedText.textContent = 'The Trakt plugin has a linked Trakt account for this user. '
            + 'Register the user with JellyNext to start syncing.';
    }

    document.getElementById('LinkSharedBtn').disabled =
        shared && (!status.traktPluginAvailable || !status.traktPluginHasToken);
}

// Show not authorized state
function showNotAuthorizedState() {
    document.getElementById('NotAuthorizedSection').style.display = 'block';
    document.getElementById('AuthorizedSection').style.display = 'none';
    document.getElementById('AuthorizingSection').style.display = 'none';
}

// Show authorized state
function showAuthorizedState() {
    document.getElementById('NotAuthorizedSection').style.display = 'none';
    document.getElementById('AuthorizedSection').style.display = 'block';
    document.getElementById('AuthorizingSection').style.display = 'none';
    loadUserSettings();
}

// Load user-specific settings
function loadUserSettings() {
    if (!JellyNextConfig.currentUserGuid) {
        return;
    }

    ApiClient.fetch({
        type: 'GET',
        url: ApiClient.getUrl('JellyNext/Trakt/Users/' + JellyNextConfig.currentUserGuid + '/Settings')
    }).then(function (response) {
        return response.json();
    }).then(function (settings) {
        document.getElementById('UserSyncMovieRecommendations').checked = settings.syncMovieRecommendations !== false;
        document.getElementById('UserSyncShowRecommendations').checked = settings.syncShowRecommendations !== false;
        document.getElementById('UserSyncNextSeasons').checked = settings.syncNextSeasons !== false;
        document.getElementById('UserNextSeasonsRecentOnly').checked = settings.nextSeasonsRecentOnly === true;
        document.getElementById('UserNextSeasonsRecentDays').value = settings.nextSeasonsRecentDays || 90;
        onNextSeasonsRecentOnlyChanged();
        document.getElementById('UserSyncWatchlistMovies').checked = settings.syncWatchlistMovies === true;
        document.getElementById('UserSyncWatchlistShows').checked = settings.syncWatchlistShows === true;
        document.getElementById('UserIgnoreCollected').checked = settings.ignoreCollected !== false;
        document.getElementById('UserIgnoreWatchlisted').checked = settings.ignoreWatchlisted === true;
        document.getElementById('UserLimitShowsToSeasonOne').checked = settings.limitShowsToSeasonOne !== false;
        document.getElementById('UserMovieRecommendationsLimit').value = settings.movieRecommendationsLimit || 50;
        document.getElementById('UserShowRecommendationsLimit').value = settings.showRecommendationsLimit || 50;
        document.getElementById('UserNotifyNewSeasonsByEmail').checked = settings.notifyNewSeasonsByEmail === true;
        document.getElementById('UserNotificationEmail').value = settings.notificationEmail || '';
        onNotifyNewSeasonsChanged();
    }).catch(function (error) {
        console.error('Error loading user settings:', error);
    });
}

// Show authorizing state
function showAuthorizingState(userCode) {
    document.getElementById('NotAuthorizedSection').style.display = 'none';
    document.getElementById('AuthorizedSection').style.display = 'none';
    document.getElementById('AuthorizingSection').style.display = 'block';
    document.getElementById('UserCodeDisplay').textContent = userCode;
}

// Check if authorization is complete
function checkAuthorizationCompletion() {
    ApiClient.fetch({
        type: 'GET',
        url: ApiClient.getUrl('JellyNext/Trakt/Users/' + JellyNextConfig.currentUserGuid + '/AuthorizationStatus')
    }).then(function (response) {
        return response.json();
    }).then(function (status) {
        if (status.isAuthorized) {
            clearInterval(JellyNextConfig.authCheckInterval);
            Dashboard.alert('Successfully linked Trakt account!');
            showAuthorizedState();
        }
    }).catch(function (error) {
        console.error('Error checking authorization completion:', error);
    });
}

// Save per-user Trakt settings
function saveUserTraktSettings(userGuid) {
    if (!userGuid) {
        return Promise.resolve();
    }

    var userSettings = {
        syncMovieRecommendations: document.getElementById('UserSyncMovieRecommendations').checked,
        syncShowRecommendations: document.getElementById('UserSyncShowRecommendations').checked,
        syncNextSeasons: document.getElementById('UserSyncNextSeasons').checked,
        nextSeasonsRecentOnly: document.getElementById('UserNextSeasonsRecentOnly').checked,
        nextSeasonsRecentDays: parseInt(document.getElementById('UserNextSeasonsRecentDays').value, 10) || 90,
        syncWatchlistMovies: document.getElementById('UserSyncWatchlistMovies').checked,
        syncWatchlistShows: document.getElementById('UserSyncWatchlistShows').checked,
        ignoreCollected: document.getElementById('UserIgnoreCollected').checked,
        ignoreWatchlisted: document.getElementById('UserIgnoreWatchlisted').checked,
        limitShowsToSeasonOne: document.getElementById('UserLimitShowsToSeasonOne').checked,
        movieRecommendationsLimit: parseInt(document.getElementById('UserMovieRecommendationsLimit').value, 10),
        showRecommendationsLimit: parseInt(document.getElementById('UserShowRecommendationsLimit').value, 10),
        notifyNewSeasonsByEmail: document.getElementById('UserNotifyNewSeasonsByEmail').checked,
        notificationEmail: document.getElementById('UserNotificationEmail').value
    };

    return ApiClient.fetch({
        type: 'POST',
        url: ApiClient.getUrl('JellyNext/Trakt/Users/' + userGuid + '/Settings'),
        data: JSON.stringify(userSettings),
        contentType: 'application/json'
    });
}
