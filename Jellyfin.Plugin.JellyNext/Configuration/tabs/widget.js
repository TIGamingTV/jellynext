// Widget tab initialization and logic

var WIDGET_POSITION = {
    TOP: 0,
    BOTTOM: 1
};

// Jellyfin serializes enums as their member name, so the saved value arrives as "Top"/"Bottom" while
// the <option> values are numeric. Assigning the name straight to the select blanks the selection,
// which then saves back as NaN -> null and fails the whole configuration update.
function normalizeWidgetPosition(value) {
    var names = {
        Top: WIDGET_POSITION.TOP,
        Bottom: WIDGET_POSITION.BOTTOM
    };

    if (typeof value === 'string' && names[value] !== undefined) {
        return names[value];
    }

    var parsed = parseInt(value, 10);
    return isNaN(parsed) ? WIDGET_POSITION.BOTTOM : parsed;
}

function initWidgetTab() {
    document.getElementById('CheckWidgetArtworkBtn').addEventListener('click', checkWidgetArtwork);
    populateWidgetDiagnosticsUsers();
    console.log('Widget tab initialized');
}

// The users dropdown is filled from the same call the rest of the page uses; it is only ready after
// the shared loadUsers() has run, so this is retried until it is.
function populateWidgetDiagnosticsUsers() {
    ApiClient.getUsers().then(function (users) {
        var selector = document.getElementById('WidgetDiagnosticsUser');
        if (!selector) {
            return;
        }

        selector.innerHTML = '';
        users.forEach(function (user) {
            var option = document.createElement('option');
            option.value = user.Id;
            option.textContent = user.Name;
            selector.appendChild(option);
        });
    });
}

function checkWidgetArtwork() {
    var userId = document.getElementById('WidgetDiagnosticsUser').value;
    var output = document.getElementById('WidgetDiagnosticsOutput');

    if (!userId) {
        Dashboard.alert('Pick a user first.');
        return;
    }

    Dashboard.showLoadingMsg();

    ApiClient.fetch({
        type: 'GET',
        url: ApiClient.getUrl('JellyNext/Widget/Diagnostics/' + userId),
        headers: { accept: 'application/json' }
    }).then(function (response) {
        return response.json();
    }).then(function (result) {
        Dashboard.hideLoadingMsg();
        output.style.display = 'block';
        output.textContent = formatWidgetDiagnostics(result);
    }).catch(function (error) {
        Dashboard.hideLoadingMsg();
        console.error('Error checking widget artwork:', error);
        Dashboard.alert('Could not check the artwork: ' + (error.message || 'unknown error'));
    });
}

function formatWidgetDiagnostics(result) {
    if (!result) {
        return 'No response.';
    }

    var lines = ['Widget enabled: ' + result.widgetEnabled];

    if (!result.items || !result.items.length) {
        lines.push('');
        lines.push('No cached Next Seasons content for this user. The widget shows nothing until the');
        lines.push('"Sync Trakt Content" task has run with "Sync Next Seasons" enabled for them.');
        return lines.join('\n');
    }

    result.items.forEach(function (item) {
        lines.push('');
        lines.push(item.Title + '  (trakt ' + item.TraktId + ', tvdb ' + (item.TvdbId || '-') +
            ', tmdb ' + (item.TmdbId || '-') + ', imdb ' + (item.ImdbId || '-') + ')');
        lines.push('  in library:   ' + (item.LibraryItemId
            ? item.LibraryItemId + ' holding [' + (item.LibraryImages || []).join(', ') + ']'
            : 'not matched'));
        lines.push('  card loads:   ' + (item.ImagePath || 'nothing'));
        lines.push('  then tries:   ' + (item.FallbackImagePath || 'nothing'));
        lines.push('  providers/Trakt resolved: ' + (item.ResolvedExternalUrl || 'nothing'));
        if (item.Error) {
            lines.push('  error: ' + item.Error);
        }
    });

    return lines.join('\n');
}

function loadWidgetSettings(config) {
    document.getElementById('NextSeasonsWidgetEnabled').checked = config.NextSeasonsWidgetEnabled === true;
    document.getElementById('NextSeasonsWidgetTitle').value = config.NextSeasonsWidgetTitle || 'New Seasons';
    document.getElementById('NextSeasonsWidgetLimit').value = config.NextSeasonsWidgetLimit || 12;
    document.getElementById('NextSeasonsWidgetPosition').value =
        normalizeWidgetPosition(config.NextSeasonsWidgetPosition);
}

function saveWidgetSettings(config) {
    config.NextSeasonsWidgetEnabled = document.getElementById('NextSeasonsWidgetEnabled').checked;

    var title = document.getElementById('NextSeasonsWidgetTitle').value.trim();
    config.NextSeasonsWidgetTitle = title || 'New Seasons';

    var limit = parseInt(document.getElementById('NextSeasonsWidgetLimit').value, 10);
    config.NextSeasonsWidgetLimit = isNaN(limit) ? 12 : Math.min(50, Math.max(1, limit));

    config.NextSeasonsWidgetPosition =
        normalizeWidgetPosition(document.getElementById('NextSeasonsWidgetPosition').value);
}
