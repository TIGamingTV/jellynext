/*
 * JellyNext - New Seasons home screen widget.
 *
 * Loaded from index.html by the plugin's script injector, so it runs on every page of the web
 * client. It draws one section into the home screen listing the shows the signed in user has a new
 * season of, each with a Request button that goes through whichever download integration the plugin
 * is configured for.
 *
 * The web client rebuilds the home screen on every navigation and offers no extension point, so the
 * section is (re)inserted by watching the DOM. All rendering is self-contained: no Jellyfin CSS
 * classes or internal modules are relied on, since those change between releases.
 */
(function () {
    'use strict';

    var ITEMS_ENDPOINT = 'JellyNext/Widget/NextSeasons';
    var REQUEST_ENDPOINT = 'JellyNext/Widget/Request';
    var SECTION_CLASS = 'jellynextSection';
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
        '.' + SECTION_CLASS + ' { margin: 0 0 1.6em; }',
        '.jellynextHeader { display: flex; align-items: baseline; margin: .8em 0 .4em; padding: 0 .6em; }',
        '.jellynextHeading { font-size: 1.3em; font-weight: 600; margin: 0; }',
        '.jellynextRow { display: flex; gap: .9em; overflow-x: auto; padding: .3em .6em 1em; scrollbar-width: thin; }',
        '.jellynextCard { flex: 0 0 auto; width: 9.6em; }',
        '.jellynextPoster { position: relative; width: 100%; aspect-ratio: 2 / 3; min-height: 8em;',
        '    border-radius: .5em; overflow: hidden; background: rgba(127,127,127,.22);',
        '    display: flex; align-items: center; justify-content: center; }',
        '.jellynextPoster img { width: 100%; height: 100%; object-fit: cover; }',
        '.jellynextInitial { font-size: 2.4em; font-weight: 600; opacity: .55; }',
        '.jellynextBadge { position: absolute; top: .4em; left: .4em; padding: .15em .45em;',
        '    border-radius: .3em; background: rgba(0,0,0,.72); color: #fff; font-size: .75em; font-weight: 600; }',
        '.jellynextName { margin-top: .45em; font-size: .95em; font-weight: 600;',
        '    white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }',
        '.jellynextMeta { font-size: .8em; opacity: .7; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }',
        '.jellynextButton { margin-top: .5em; width: 100%; padding: .5em .2em; border: 0; border-radius: .3em;',
        '    font-size: .85em; font-weight: 600; cursor: pointer; color: #fff;',
        '    background: var(--accent, #00a4dc); font-family: inherit; }',
        '.jellynextButton:hover:not(:disabled) { filter: brightness(1.12); }',
        '.jellynextButton:disabled { background: rgba(127,127,127,.3); color: inherit; opacity: .85; cursor: default; }'
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
        var parts = [];
        if (item.year) {
            parts.push(String(item.year));
        }

        var episodes = episodeText(item);
        if (episodes) {
            parts.push(episodes);
        }

        return parts.join(' · ');
    }

    function buildPoster(item) {
        var poster = document.createElement('div');
        poster.className = 'jellynextPoster';

        if (item.imagePath) {
            var image = document.createElement('img');
            image.loading = 'lazy';
            image.alt = '';
            image.src = ApiClient.getUrl(item.imagePath);
            image.addEventListener('error', function () {
                image.remove();
                poster.appendChild(buildInitial(item));
            });
            poster.appendChild(image);
        } else {
            poster.appendChild(buildInitial(item));
        }

        var badge = document.createElement('span');
        badge.className = 'jellynextBadge';
        badge.textContent = 'S' + item.seasonNumber;
        poster.appendChild(badge);

        return poster;
    }

    function buildInitial(item) {
        var initial = document.createElement('span');
        initial.className = 'jellynextInitial';
        initial.textContent = (item.title || '?').charAt(0).toUpperCase();
        return initial;
    }

    function buildCard(item) {
        var card = document.createElement('div');
        card.className = 'jellynextCard';
        card.appendChild(buildPoster(item));

        var name = document.createElement('div');
        name.className = 'jellynextName';
        name.textContent = item.title;
        name.title = item.title;
        card.appendChild(name);

        var meta = document.createElement('div');
        meta.className = 'jellynextMeta';
        meta.textContent = metaText(item);
        card.appendChild(meta);

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

        card.appendChild(button);
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
        section.className = SECTION_CLASS + ' verticalSection';
        section.style.display = 'none';

        var header = document.createElement('div');
        header.className = 'jellynextHeader';

        var heading = document.createElement('h2');
        heading.className = 'jellynextHeading sectionTitle';
        header.appendChild(heading);
        section.appendChild(header);

        var row = document.createElement('div');
        row.className = 'jellynextRow';
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
            if (container.querySelector('.' + SECTION_CLASS)) {
                return;
            }

            addStyles();
            var section = buildSection();
            if (state.data && state.data.position === 'Bottom') {
                container.appendChild(section);
            } else {
                container.insertBefore(section, container.firstChild);
            }

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

                // The placement setting is only known after the first fetch; move the section then.
                if (data && data.position === 'Bottom' && section.parentNode
                    && section.parentNode.lastChild !== section) {
                    section.parentNode.appendChild(section);
                }

                fill(section, data);
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
