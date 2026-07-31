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
    console.log('Widget tab initialized');
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
