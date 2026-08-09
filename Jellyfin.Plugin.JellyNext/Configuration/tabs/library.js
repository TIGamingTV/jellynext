// Library tab initialization and logic

function initLibraryTab() {
    var tagging = document.getElementById('MediaTaggingEnabled');
    if (tagging) {
        tagging.addEventListener('change', updateLibraryTabState);
    }

    console.log('Library tab initialized');
}

// The badge and the icon are drawn from the tag, so without it they do nothing at all. Greying them
// out says so before the user saves and wonders why nothing changed.
function updateLibraryTabState() {
    var enabled = document.getElementById('MediaTaggingEnabled').checked;
    var dependents = [
        'MediaTagName',
        'MediaBadgeEnabled',
        'MediaBadgeText',
        'MediaRequestIconEnabled',
        'MediaRequestButtonText'
    ];

    dependents.forEach(function (id) {
        var element = document.getElementById(id);
        if (element) {
            element.disabled = !enabled;
        }
    });
}

function loadLibrarySettings(config) {
    document.getElementById('MediaTaggingEnabled').checked = config.MediaTaggingEnabled === true;
    document.getElementById('MediaTagName').value = config.MediaTagName || 'JellyNext';
    document.getElementById('MediaBadgeEnabled').checked = config.MediaBadgeEnabled !== false;
    document.getElementById('MediaBadgeText').value = config.MediaBadgeText || '';
    document.getElementById('MediaRequestIconEnabled').checked = config.MediaRequestIconEnabled !== false;
    document.getElementById('MediaRequestButtonText').value = config.MediaRequestButtonText || 'Request';

    updateLibraryTabState();
}

function saveLibrarySettings(config) {
    config.MediaTaggingEnabled = document.getElementById('MediaTaggingEnabled').checked;

    // An empty tag would mean "tag everything with nothing", which the server would have to reject
    // anyway - fall back rather than fail the whole configuration save over it.
    var tag = document.getElementById('MediaTagName').value.trim();
    config.MediaTagName = tag || 'JellyNext';

    config.MediaBadgeEnabled = document.getElementById('MediaBadgeEnabled').checked;
    config.MediaBadgeText = document.getElementById('MediaBadgeText').value.trim();
    config.MediaRequestIconEnabled = document.getElementById('MediaRequestIconEnabled').checked;

    var buttonText = document.getElementById('MediaRequestButtonText').value.trim();
    config.MediaRequestButtonText = buttonText || 'Request';
}
