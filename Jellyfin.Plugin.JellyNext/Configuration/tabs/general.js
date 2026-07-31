// General tab initialization and logic

function initGeneralTab() {
    // General tab has no specific initialization logic
    // All inputs are handled by the global form load/save logic
    console.log('General tab initialized');
}

function loadGeneralSettings(config) {
    document.getElementById('CacheExpirationHours').value = config.CacheExpirationHours || 6;
    document.getElementById('UseShortDummyVideo').checked = config.UseShortDummyVideo !== false;
    document.getElementById('PlaybackStopDelaySeconds').value = config.PlaybackStopDelaySeconds || 2;
}

// Defaults matter on save, not just on load: an empty number field parses to NaN, which serializes
// to null, and the server rejects null for a non-nullable int - taking the whole save with it.
function saveGeneralSettings(config) {
    config.CacheExpirationHours = parseInt(document.getElementById('CacheExpirationHours').value, 10) || 6;
    config.UseShortDummyVideo = document.getElementById('UseShortDummyVideo').checked;
    var stopDelay = parseInt(document.getElementById('PlaybackStopDelaySeconds').value, 10);
    config.PlaybackStopDelaySeconds = isNaN(stopDelay) ? 2 : stopDelay;
}
