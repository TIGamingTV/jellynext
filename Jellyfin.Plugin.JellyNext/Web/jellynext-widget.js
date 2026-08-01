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
 * cardImageContainer / cardText) so they inherit the client's theme - fonts, colours, corners,
 * hover. Their geometry, though, is set here rather than inherited: the width of a home screen card
 * comes from a shape class (.overflowPortraitCard and friends) that not every client applies, and
 * where it is missing the card collapses to the width of its title and the artwork disappears
 * entirely - which is what the desktop app was showing. Owning the width and the aspect ratio costs
 * a handful of media queries and makes the row look the same everywhere.
 *
 * The cards are portrait because what is being offered is a season, and a season's picture is a
 * poster wherever Jellyfin and the metadata providers have one.
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

    // Every geometry rule is scoped under .jellynextCard so it outranks the client's single-class
    // card rules whether or not they are present - the stylesheet is appended to <head> after them,
    // and two classes beat one.
    var STYLES = [
        '.jellynextRow { display: flex; flex-wrap: nowrap; align-items: flex-start;',
        '    overflow-x: auto; scrollbar-width: thin; }',

        // Card size. Mirrors the proportions of Jellyfin's own portrait rows, but does not depend on
        // the client applying a shape class for the card to have a width at all.
        '.jellynextCard { box-sizing: border-box; flex: 0 0 auto; width: 42vw; }',
        '@media (min-width: 40em) { .jellynextCard { width: 27vw; } }',
        '@media (min-width: 50em) { .jellynextCard { width: 22vw; } }',
        '@media (min-width: 60em) { .jellynextCard { width: 17vw; } }',
        '@media (min-width: 80em) { .jellynextCard { width: 14vw; } }',
        '@media (min-width: 100em) { .jellynextCard { width: 12vw; } }',
        '@media (min-width: 120em) { .jellynextCard { width: 10vw; } }',

        '.jellynextCard .cardBox { margin: 0 .3em; }',
        '.jellynextCard .cardScalable { position: relative; display: block; width: 100%; }',
        '.jellynextCard .cardPadder { padding-bottom: 150%; }',
        '.jellynextCard .cardText { white-space: nowrap; overflow: hidden; text-overflow: ellipsis;',
        '    padding: .1em 0; }',
        '.jellynextCard .cardText-secondary { font-size: 86%; opacity: .75; }',

        // The artwork, inside Jellyfin's image container.
        '.jellynextCard .jellynextImage { position: absolute; top: 0; left: 0; right: 0; bottom: 0;',
        '    overflow: hidden; border-radius: .2em; display: flex; align-items: center;',
        '    justify-content: center; background: rgba(127,127,127,.22); }',
        '.jellynextCard .jellynextImage img { width: 100%; height: 100%; object-fit: cover;',
        '    display: block; }',
        '.jellynextCard .jellynextImage img.jellynextContain { object-fit: contain; }',
        '.jellynextPlaceholder { padding: .6em; font-size: 1em; font-weight: 600; opacity: .6;',
        '    text-align: center; line-height: 1.25; overflow: hidden; display: -webkit-box;',
        '    -webkit-line-clamp: 3; -webkit-box-orient: vertical; }',
        '.jellynextCard .jellynextBadge { position: absolute; top: .4em; left: .4em; z-index: 1;',
        '    padding: .15em .45em; border-radius: .3em; background: rgba(0,0,0,.72); color: #fff;',
        '    font-size: .8em; font-weight: 600; }',

        '.jellynextButton { display: block; width: 100%; margin-top: .5em; padding: .5em .2em;',
        '    border: 0; border-radius: .3em; font-size: .85em; font-weight: 600; cursor: pointer;',
        '    color: #fff; background: var(--accent, #00a4dc); font-family: inherit; }',
        '.jellynextButton:hover:not(:disabled) { filter: brightness(1.12); }',
        '.jellynextButton:disabled { background: rgba(127,127,127,.3); color: inherit; opacity: .85;',
        '    cursor: default; }'
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

    /**
     * Shown when neither the library nor Trakt has artwork. The show's name reads better than an
     * initial: a tile saying "T" tells nobody which show it is.
     */
    function buildPlaceholder(item) {
        var placeholder = document.createElement('span');
        placeholder.className = 'jellynextPlaceholder';
        placeholder.textContent = item.title || '';
        return placeholder;
    }

    function buildImage(item) {
        var container = document.createElement('div');
        container.className = 'cardImageContainer coveredImage cardContent jellynextImage';

        var sources = [item.imagePath, item.fallbackImagePath].filter(Boolean);

        if (sources.length) {
            var attempt = 0;
            var image = document.createElement('img');
            image.loading = 'lazy';
            image.alt = '';
            image.decoding = 'async';
            image.addEventListener('load', function () {
                // The card is portrait, so a backdrop or a thumbnail - which is what the fallbacks
                // come back as - would be cropped down to a strip of itself. Show it whole instead.
                if (image.naturalWidth > image.naturalHeight * 1.1) {
                    image.classList.add('jellynextContain');
                }
            });
            image.addEventListener('error', function () {
                attempt += 1;
                if (attempt < sources.length) {
                    image.src = ApiClient.getUrl(sources[attempt]);
                    return;
                }

                image.remove();
                container.insertBefore(buildPlaceholder(item), container.firstChild);
            });
            image.src = ApiClient.getUrl(sources[0]);
            container.appendChild(image);
        } else {
            container.appendChild(buildPlaceholder(item));
        }

        var badge = document.createElement('span');
        badge.className = 'jellynextBadge';
        badge.textContent = 'S' + item.seasonNumber;
        container.appendChild(badge);

        return container;
    }

    function buildCard(item) {
        var card = document.createElement('div');
        card.className = 'card overflowPortraitCard jellynextCard';

        var box = document.createElement('div');
        box.className = 'cardBox cardBox-bottompadded';

        var scalable = document.createElement('div');
        scalable.className = 'cardScalable';

        var padder = document.createElement('div');
        padder.className = 'cardPadder cardPadder-overflowPortrait';
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
