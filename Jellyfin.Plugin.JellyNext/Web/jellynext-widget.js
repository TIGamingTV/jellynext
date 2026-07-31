/*
 * JellyNext - New Seasons home screen widget.
 *
 * Loaded from index.html by the plugin's script injector, so it runs on every page of the web
 * client. It draws one section into the home screen listing the shows the signed in user has a new
 * season of, each with a Request button that goes through whichever download integration the plugin
 * is configured for.
 *
 * The web client rebuilds the home screen on every navigation and offers no extension point, so the
 * section is (re)inserted by watching the DOM.
 *
 * Cards are built from Jellyfin's own card markup (card / cardBox / cardScalable / cardPadder /
 * cardImageContainer / cardText) so the row is the same size, shape and alignment as the home
 * screen's own rows in whatever theme and viewport the user has - reimplementing that means copying
 * a stack of viewport media queries and getting it subtly wrong. Only the request button and the
 * season badge are styled here. If those classes ever stop existing, a probe detects it and a
 * self-contained fallback stylesheet takes over.
 */
(function () {
    'use strict';

    var ITEMS_ENDPOINT = 'JellyNext/Widget/NextSeasons';
    var REQUEST_ENDPOINT = 'JellyNext/Widget/Request';
    var SECTION_CLASS = 'jellynextSection';
    var FALLBACK_CLASS = 'jellynextFallback';
    var STYLE_ID = 'jellynextWidgetStyles';
    var DATA_TTL_MS = 5 * 60 * 1000;
    var RESCAN_DELAY_MS = 300;

    var state = {
        data: null,
        fetchedAt: 0,
        userId: null,
        pending: null,
        scheduled: false
    };

    var STYLES = [
        // Ours: the row itself, the artwork inside Jellyfin's image container, and the button.
        '.jellynextRow { display: flex; flex-wrap: nowrap; overflow-x: auto; scrollbar-width: thin; }',
        '.jellynextImage { display: flex; align-items: center; justify-content: center;',
        '    background: rgba(127,127,127,.22); }',
        '.jellynextImage img { width: 100%; height: 100%; object-fit: cover; display: block; }',
        '.jellynextInitial { font-size: 2.6em; font-weight: 600; opacity: .55; }',
        '.jellynextImage .jellynextBadge { position: absolute; top: .4em; left: .4em; z-index: 1;',
        '    padding: .15em .45em; border-radius: .3em; background: rgba(0,0,0,.72); color: #fff;',
        '    font-size: .8em; font-weight: 600; }',
        '.jellynextButton { display: block; width: 100%; margin-top: .5em; padding: .5em .2em;',
        '    border: 0; border-radius: .3em; font-size: .85em; font-weight: 600; cursor: pointer;',
        '    color: #fff; background: var(--accent, #00a4dc); font-family: inherit; }',
        '.jellynextButton:hover:not(:disabled) { filter: brightness(1.12); }',
        '.jellynextButton:disabled { background: rgba(127,127,127,.3); color: inherit; opacity: .85;',
        '    cursor: default; }',

        // Only used when the web client's card stylesheet is not there to size the cards. Mirrors
        // what those classes normally provide, so the widget degrades to a plain but tidy row.
        '.' + FALLBACK_CLASS + ' .jellynextHeading { font-size: 1.3em; font-weight: 600;',
        '    margin: .8em 0 .4em; padding: 0 .6em; }',
        '.' + FALLBACK_CLASS + ' .jellynextRow { padding: 0 .6em 1em; }',
        '.' + FALLBACK_CLASS + ' .jellynextCard { flex: 0 0 auto; width: 15em; }',
        '.' + FALLBACK_CLASS + ' .cardBox { margin: .6em; }',
        '.' + FALLBACK_CLASS + ' .cardScalable { position: relative; }',
        '.' + FALLBACK_CLASS + ' .cardPadder { padding-bottom: 56.25%; }',
        '.' + FALLBACK_CLASS + ' .cardImageContainer { position: absolute; top: 0; left: 0; right: 0;',
        '    bottom: 0; overflow: hidden; border-radius: .2em; }',
        '.' + FALLBACK_CLASS + ' .cardText { white-space: nowrap; overflow: hidden;',
        '    text-overflow: ellipsis; padding: .06em 2px; }',
        '.' + FALLBACK_CLASS + ' .cardText-secondary { font-size: 86%; opacity: .75; }'
    ].join('\n');

    function addStyles() {
        if (document.getElementById(STYLE_ID)) {
            return;
        }

        var style = document.createElement('style');
        style.id = STYLE_ID;
        style.textContent = STYLES;
        document.head.appendChild(style);
    }

    /**
     * Detects whether the web client's card stylesheet is present, by measuring a class whose only
     * job is to give a card its aspect ratio.
     */
    function hasNativeCardStyles() {
        var probe = document.createElement('div');
        probe.className = 'cardPadder cardPadder-overflowBackdrop';
        probe.style.cssText = 'position:absolute;visibility:hidden;width:100px;top:-1000px;';
        document.body.appendChild(probe);
        var padding = parseFloat(window.getComputedStyle(probe).paddingBottom) || 0;
        probe.remove();
        return padding > 0;
    }

    function isSignedIn() {
        return !!(window.ApiClient
            && typeof ApiClient.getUrl === 'function'
            && ApiClient.accessToken
            && ApiClient.accessToken()
            && ApiClient.getCurrentUserId
            && ApiClient.getCurrentUserId());
    }

    function apiFetch(options) {
        return ApiClient.fetch(options).then(function (response) {
            // ApiClient hands back the raw response for these calls; a plain body is possible on error.
            if (response && typeof response.json === 'function') {
                return response.json().then(function (body) {
                    if (response.ok === false) {
                        throw new Error((body && body.message) || response.statusText);
                    }

                    return body;
                });
            }

            return response;
        });
    }

    function loadData(force) {
        var userId = ApiClient.getCurrentUserId();
        if (userId !== state.userId) {
            state.userId = userId;
            state.data = null;
        }

        if (!force && state.data && (Date.now() - state.fetchedAt) < DATA_TTL_MS) {
            return Promise.resolve(state.data);
        }

        if (state.pending) {
            return state.pending;
        }

        state.pending = apiFetch({
            type: 'GET',
            url: ApiClient.getUrl(ITEMS_ENDPOINT),
            headers: { accept: 'application/json' }
        }).then(function (data) {
            state.pending = null;
            state.data = data;
            state.fetchedAt = Date.now();
            return data;
        }).catch(function (error) {
            state.pending = null;
            console.error('[JellyNext] Could not load new seasons', error);
            return null;
        });

        return state.pending;
    }

    function episodeText(item) {
        var total = item.episodeCount;
        var aired = item.airedEpisodes;

        if (item.isAiring && aired && total && aired < total) {
            return aired + ' of ' + total + ' episodes';
        }

        if (total) {
            return total + (total === 1 ? ' episode' : ' episodes');
        }

        if (aired) {
            return aired + (aired === 1 ? ' episode' : ' episodes');
        }

        return '';
    }

    function metaText(item) {
        var parts = ['Season ' + item.seasonNumber];
        if (item.year) {
            parts.push(String(item.year));
        }

        var episodes = episodeText(item);
        if (episodes) {
            parts.push(episodes);
        }

        return parts.join(' · ');
    }

    function buildInitial(item) {
        var initial = document.createElement('span');
        initial.className = 'jellynextInitial';
        initial.textContent = (item.title || '?').charAt(0).toUpperCase();
        return initial;
    }

    function buildImage(item) {
        var container = document.createElement('div');
        container.className = 'cardImageContainer coveredImage cardContent jellynextImage';

        if (item.imagePath) {
            var image = document.createElement('img');
            image.loading = 'lazy';
            image.alt = '';
            image.src = ApiClient.getUrl(item.imagePath);
            image.addEventListener('error', function () {
                image.remove();
                container.insertBefore(buildInitial(item), container.firstChild);
            });
            container.appendChild(image);
        } else {
            container.appendChild(buildInitial(item));
        }

        var badge = document.createElement('span');
        badge.className = 'jellynextBadge';
        badge.textContent = 'S' + item.seasonNumber;
        container.appendChild(badge);

        return container;
    }

    function buildCard(item) {
        var card = document.createElement('div');
        card.className = 'card overflowBackdropCard jellynextCard';

        var box = document.createElement('div');
        box.className = 'cardBox cardBox-bottompadded';

        var scalable = document.createElement('div');
        scalable.className = 'cardScalable';

        var padder = document.createElement('div');
        padder.className = 'cardPadder cardPadder-overflowBackdrop';
        scalable.appendChild(padder);
        scalable.appendChild(buildImage(item));
        box.appendChild(scalable);

        var name = document.createElement('div');
        name.className = 'cardText cardText-first jellynextName';
        name.textContent = item.title;
        name.title = item.title;
        box.appendChild(name);

        var meta = document.createElement('div');
        meta.className = 'cardText cardText-secondary jellynextMeta';
        meta.textContent = metaText(item);
        box.appendChild(meta);

        var button = document.createElement('button');
        button.className = 'jellynextButton';
        button.type = 'button';
        if (item.requested) {
            button.textContent = 'Requested';
            button.disabled = true;
        } else {
            button.textContent = 'Request';
            button.addEventListener('click', function () {
                requestSeason(item, button);
            });
        }

        box.appendChild(button);
        card.appendChild(box);
        return card;
    }

    function requestSeason(item, button) {
        button.disabled = true;
        button.textContent = 'Requesting…';

        apiFetch({
            type: 'POST',
            url: ApiClient.getUrl(REQUEST_ENDPOINT),
            data: JSON.stringify({ traktId: item.traktId, seasonNumber: item.seasonNumber }),
            contentType: 'application/json',
            headers: { accept: 'application/json' }
        }).then(function (result) {
            if (result && result.success === false) {
                throw new Error(result.message || 'The request was not accepted.');
            }

            item.requested = true;
            button.textContent = 'Requested';
            button.title = (result && result.message) || '';
            state.fetchedAt = 0;
        }).catch(function (error) {
            console.error('[JellyNext] Request failed', error);
            button.disabled = false;
            button.textContent = 'Request';
            var message = (error && error.message) || 'The request could not be sent.';
            if (window.Dashboard && typeof Dashboard.alert === 'function') {
                Dashboard.alert({ title: 'JellyNext', message: message });
            }
        });
    }

    function buildSection() {
        var section = document.createElement('div');
        section.className = 'verticalSection ' + SECTION_CLASS;
        section.style.display = 'none';

        if (!hasNativeCardStyles()) {
            section.classList.add(FALLBACK_CLASS);
        }

        var heading = document.createElement('h2');
        heading.className = 'sectionTitle sectionTitle-cards padded-left jellynextHeading';
        section.appendChild(heading);

        var row = document.createElement('div');
        row.className = 'itemsContainer scrollSlider focuscontainer-x padded-left padded-right jellynextRow';
        section.appendChild(row);

        return section;
    }

    function fill(section, data) {
        var heading = section.querySelector('.jellynextHeading');
        var row = section.querySelector('.jellynextRow');

        if (!data || data.enabled === false || !data.items || !data.items.length) {
            section.style.display = 'none';
            row.textContent = '';
            return;
        }

        heading.textContent = data.title || 'New Seasons';
        row.textContent = '';
        data.items.forEach(function (item) {
            row.appendChild(buildCard(item));
        });

        section.style.display = '';
    }

    function findContainers() {
        var containers = document.querySelectorAll('.homeSectionsContainer');
        if (containers.length) {
            return containers;
        }

        return document.querySelectorAll('#homeTab .sections, #indexPage .sections');
    }

    function wantsBottom() {
        return !!(state.data && state.data.position === 'Bottom');
    }

    /**
     * Keeps the section at the requested end of the home screen. Sections are appended as their
     * content loads, so a section placed at the bottom on insertion can end up in the middle a
     * moment later; this runs on every scan until the order settles.
     */
    function place(container, section) {
        if (wantsBottom()) {
            if (container.lastElementChild !== section) {
                container.appendChild(section);
            }
        } else if (container.firstElementChild !== section) {
            container.insertBefore(section, container.firstElementChild);
        }
    }

    function ensureSections() {
        if (!isSignedIn()) {
            return;
        }

        var containers = findContainers();
        if (!containers.length) {
            return;
        }

        var added = [];
        Array.prototype.forEach.call(containers, function (container) {
            var section = container.querySelector('.' + SECTION_CLASS);
            if (section) {
                place(container, section);
                return;
            }

            addStyles();
            section = buildSection();
            container.appendChild(section);
            place(container, section);
            added.push(section);
        });

        if (!added.length) {
            return;
        }

        loadData(false).then(function (data) {
            added.forEach(function (section) {
                if (!section.isConnected) {
                    return;
                }

                fill(section, data);

                if (section.parentNode) {
                    place(section.parentNode, section);
                }
            });
        });
    }

    function scheduleScan() {
        if (state.scheduled) {
            return;
        }

        state.scheduled = true;
        setTimeout(function () {
            state.scheduled = false;
            try {
                ensureSections();
            } catch (error) {
                console.error('[JellyNext] Widget failed', error);
            }
        }, RESCAN_DELAY_MS);
    }

    function start() {
        new MutationObserver(scheduleScan).observe(document.body, { childList: true, subtree: true });
        scheduleScan();
    }

    if (document.body) {
        start();
    } else {
        document.addEventListener('DOMContentLoaded', start);
    }
})();
