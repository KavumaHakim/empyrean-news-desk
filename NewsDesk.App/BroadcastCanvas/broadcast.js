// NEWS DESK - Broadcast Engine Script
// Strictly enforces 500ms transitions, dual buffering, continuous ticker, and WebView2 messaging.

(function () {
    'use strict';

    // State
    let activeSlot = 'a'; // 'a' or 'b'
    let isTransitioning = false;
    let isStandby = true;
    let transitionDurationMs = 500;

    // Elements
    const sceneA = document.getElementById('scene-a');
    const sceneB = document.getElementById('scene-b');
    const videoA = document.getElementById('video-a');
    const videoB = document.getElementById('video-b');
    const graphicA = document.getElementById('graphic-a');
    const graphicB = document.getElementById('graphic-b');

    const liveIndicator = document.getElementById('live-indicator');
    const lowerThird = document.getElementById('lower-third');
    const ltCategory = document.getElementById('lt-category');
    const ltBreakingTag = document.getElementById('lt-breaking-tag');
    const ltLocation = document.getElementById('lt-location');
    const ltTitle = document.getElementById('lt-title');
    const ltSubtitle = document.getElementById('lt-subtitle');
    const ltReporterRow = document.getElementById('lt-reporter-row');
    const ltReporterName = document.getElementById('lt-reporter-name');
    const ltReporterTitle = document.getElementById('lt-reporter-title');

    const newsTicker = document.getElementById('news-ticker');
    const tickerBadgeText = document.getElementById('ticker-badge-text');
    const tickerTrack = document.getElementById('ticker-track');

    const layerStandby = document.getElementById('layer-standby');
    const standbyClock = document.getElementById('standby-clock');
    const testOverlay = document.getElementById('test-overlay');

    // Standby Clock
    function updateClock() {
        const now = new Date();
        const hrs = String(now.getHours()).padStart(2, '0');
        const mins = String(now.getMinutes()).padStart(2, '0');
        const secs = String(now.getSeconds()).padStart(2, '0');
        if (standbyClock) {
            standbyClock.textContent = `${hrs}:${mins}:${secs}`;
        }
    }
    setInterval(updateClock, 1000);
    updateClock();

    // Host communication helper
    function sendHostMessage(type, payload) {
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage({ type: type, data: payload });
        }
    }

    // Video Telemetry (progress & timecode)
    function setupVideoTelemetry(video, slotName) {
        video.addEventListener('timeupdate', () => {
            if (activeSlot === slotName && !isTransitioning) {
                sendHostMessage('timeupdate', {
                    currentTime: video.currentTime,
                    duration: video.duration || 0,
                    paused: video.paused,
                    ended: video.ended
                });
            }
        });

        video.addEventListener('ended', () => {
            if (activeSlot === slotName) {
                sendHostMessage('ended', { slot: slotName });
            }
        });

        video.addEventListener('error', (e) => {
            sendHostMessage('error', {
                message: `Video playback error in slot ${slotName}`,
                code: video.error ? video.error.code : -1
            });
        });
    }

    setupVideoTelemetry(videoA, 'a');
    setupVideoTelemetry(videoB, 'b');

    // Helpers
    function getActiveVideo() {
        return activeSlot === 'a' ? videoA : videoB;
    }

    function getStandbyVideo() {
        return activeSlot === 'a' ? videoB : videoA;
    }

    function getActiveScene() {
        return activeSlot === 'a' ? sceneA : sceneB;
    }

    function getStandbyScene() {
        return activeSlot === 'a' ? sceneB : sceneA;
    }

    function getStandbyGraphic() {
        return activeSlot === 'a' ? graphicB : graphicA;
    }

    // Prepare content in standby slot without touching active broadcast
    let scenePreparedTimer = null;
    function loadScene(data) {
        const standbyVid = getStandbyVideo();
        const standbyGraph = getStandbyGraphic();
        const standbySlot = activeSlot === 'a' ? 'b' : 'a';

        let preparedSent = false;
        const markPrepared = () => {
            if (preparedSent) return;
            preparedSent = true;
            sendHostMessage('scene_prepared', { slot: standbySlot });
        };

        // A superseded loadScene must not let its fallback timer fire later and
        // prematurely confirm the NEW load as ready
        if (scenePreparedTimer !== null) {
            clearTimeout(scenePreparedTimer);
            scenePreparedTimer = null;
        }

        if (data.type === 'video' && data.mediaUrl) {
            standbyVid.style.display = 'block';
            standbyGraph.style.display = 'none';

            // Report prepared only once the media can actually render, so the host
            // never takes live a scene that would flash black mid-load.
            standbyVid.addEventListener('loadeddata', markPrepared, { once: true });
            standbyVid.addEventListener('error', markPrepared, { once: true });
            scenePreparedTimer = setTimeout(markPrepared, 4000);

            standbyVid.src = data.mediaUrl;
            standbyVid.volume = 0; // muted while standing by
            standbyVid.load();
            if (data.autoPlay) {
                standbyVid.play().catch(() => {});
            }
        } else if (data.type === 'graphic') {
            standbyVid.style.display = 'none';
            standbyVid.pause();
            standbyVid.removeAttribute('src');
            standbyVid.load();

            standbyGraph.style.display = 'flex';
            renderGraphicSlide(standbyGraph, data.graphic || data);
            markPrepared();
        } else {
            markPrepared();
        }
    }

    // Escapes operator-provided text before it is injected as HTML
    function escapeHtml(value) {
        return String(value == null ? '' : value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    function renderGraphicSlide(container, graphic) {
        const title = graphic.title || 'HEADLINE';
        const subtitle = graphic.subtitle || '';
        const category = graphic.category || 'SPECIAL REPORT';
        const bg = graphic.backgroundStyle || 'navy';

        container.innerHTML = `
            <div class="fullscreen-slide ${bg === 'breaking' ? 'slide-breaking' : ''}">
                <div class="slide-badge">${escapeHtml(category)}</div>
                <h1 class="slide-title">${escapeHtml(title)}</h1>
                <p class="slide-subtitle">${escapeHtml(subtitle)}</p>
            </div>
        `;
    }

    // Audio Crossfader (linear volume ramp over transitionDurationMs)
    function crossfadeAudio(outgoingVid, incomingVid, durationMs) {
        if (durationMs <= 0) {
            if (outgoingVid) outgoingVid.volume = 0;
            if (incomingVid) incomingVid.volume = 1;
            return;
        }

        const stepInterval = 25; // 25ms steps
        const totalSteps = durationMs / stepInterval;
        let step = 0;

        if (incomingVid) incomingVid.volume = 0;
        if (outgoingVid) outgoingVid.volume = 1;

        const interval = setInterval(() => {
            step++;
            const progress = Math.min(1, step / totalSteps);

            if (incomingVid) {
                incomingVid.volume = Math.max(0, Math.min(1, progress));
            }
            if (outgoingVid) {
                outgoingVid.volume = Math.max(0, Math.min(1, 1 - progress));
            }

            if (step >= totalSteps) {
                clearInterval(interval);
                if (outgoingVid) outgoingVid.volume = 0;
                if (incomingVid) incomingVid.volume = 1;
            }
        }, stepInterval);
    }

    // Clean transition classes
    function cleanAnimationClasses(el) {
        el.classList.remove(
            'anim-fade-out', 'anim-fade-in',
            'anim-dissolve-out', 'anim-dissolve-in',
            'anim-slide-left-out', 'anim-slide-left-in',
            'anim-slide-right-out', 'anim-slide-right-in',
            'anim-wipe-out', 'anim-wipe-in',
            'anim-zoom-out', 'anim-zoom-in',
            'transition-cut-in', 'transition-cut-out'
        );
    }

    // Execute transition between scenes
    function takeLive(data) {
        if (isTransitioning) return; // Prevent concurrent corruption
        isTransitioning = true;

        const transition = (data && data.transition) ? data.transition.toLowerCase().replace(/\s+/g, '-') : 'fade';
        const duration = transition === 'cut' ? 0 : 500; // Strict duration rule: 500ms or 0ms

        const outgoingScene = getActiveScene();
        const incomingScene = getStandbyScene();
        const outgoingVideo = getActiveVideo();
        const incomingVideo = getStandbyVideo();

        // Ensure incoming video is playing if active
        if (incomingVideo.src && incomingVideo.style.display !== 'none') {
            incomingVideo.play().catch(() => {});
        }

        // Handle Audio
        crossfadeAudio(outgoingVideo, incomingVideo, duration);

        if (transition === 'cut') {
            // Instantaneous switch
            outgoingScene.className = 'scene-slot is-standby';
            incomingScene.className = 'scene-slot is-active';
            cleanAnimationClasses(outgoingScene);
            cleanAnimationClasses(incomingScene);

            if (outgoingVideo.src) {
                outgoingVideo.pause();
            }

            activeSlot = activeSlot === 'a' ? 'b' : 'a';
            isTransitioning = false;
            sendHostMessage('transition_complete', { activeSlot: activeSlot, transition: 'cut' });
            return;
        }

        // Setup 500ms animated transitions
        cleanAnimationClasses(outgoingScene);
        cleanAnimationClasses(incomingScene);

        incomingScene.classList.add('is-active');
        incomingScene.classList.remove('is-standby');

        let outClass = '';
        let inClass = '';

        switch (transition) {
            case 'fade':
                outClass = 'anim-fade-out';
                inClass = 'anim-fade-in';
                break;
            case 'dissolve':
                outClass = 'anim-dissolve-out';
                inClass = 'anim-dissolve-in';
                break;
            case 'slide-left':
                outClass = 'anim-slide-left-out';
                inClass = 'anim-slide-left-in';
                break;
            case 'slide-right':
                outClass = 'anim-slide-right-out';
                inClass = 'anim-slide-right-in';
                break;
            case 'wipe':
                outClass = 'anim-wipe-out';
                inClass = 'anim-wipe-in';
                break;
            case 'zoom':
                outClass = 'anim-zoom-out';
                inClass = 'anim-zoom-in';
                break;
            default:
                outClass = 'anim-dissolve-out';
                inClass = 'anim-dissolve-in';
                break;
        }

        outgoingScene.classList.add(outClass);
        incomingScene.classList.add(inClass);

        // Strict 500ms timeout
        setTimeout(() => {
            cleanAnimationClasses(outgoingScene);
            cleanAnimationClasses(incomingScene);

            outgoingScene.className = 'scene-slot is-standby';
            incomingScene.className = 'scene-slot is-active';

            if (outgoingVideo.src) {
                outgoingVideo.pause();
                outgoingVideo.currentTime = 0;
            }

            activeSlot = activeSlot === 'a' ? 'b' : 'a';
            isTransitioning = false;
            sendHostMessage('transition_complete', { activeSlot: activeSlot, transition: transition });
        }, 500);
    }

    // Playback Controls
    function play() {
        const vid = getActiveVideo();
        if (vid && vid.src) vid.play().catch(() => {});
    }

    function pause() {
        const vid = getActiveVideo();
        if (vid && vid.src) vid.pause();
    }

    function stop() {
        const vid = getActiveVideo();
        if (vid && vid.src) {
            vid.pause();
            vid.currentTime = 0;
        }
    }

    function seek(seconds) {
        const vid = getActiveVideo();
        if (vid && vid.src) {
            vid.currentTime = seconds;
        }
    }

    function setVolume(val) {
        const vid = getActiveVideo();
        if (vid) vid.volume = Math.max(0, Math.min(1, val));
    }

    // Test overlay: rehearse a transition effect WITHOUT swapping the active slot,
    // so the on-air broadcast is never affected by the operator's transition tests.
    const IN_CLASS_BY_TRANSITION = {
        'fade': 'anim-fade-in',
        'dissolve': 'anim-dissolve-in',
        'slide-left': 'anim-slide-left-in',
        'slide-right': 'anim-slide-right-in',
        'wipe': 'anim-wipe-in',
        'zoom': 'anim-zoom-in'
    };

    // Cleanup handle so a rapid second test click cancels the first test's cleanup
    let testCleanupTimer = null;

    function testTransition(data) {
        if (!testOverlay) return;
        const transition = (data && data.transition) ? String(data.transition).toLowerCase() : 'fade';

        if (testCleanupTimer !== null) {
            clearTimeout(testCleanupTimer);
            testCleanupTimer = null;
        }
        cleanAnimationClasses(testOverlay);
        testOverlay.style.opacity = '';

        if (transition === 'cut') {
            // Instantaneous flash (0 ms rule: no animation)
            testOverlay.style.opacity = '1';
            testCleanupTimer = setTimeout(() => { testOverlay.style.opacity = ''; }, 60);
            return;
        }

        const inClass = IN_CLASS_BY_TRANSITION[transition] || 'anim-dissolve-in';
        void testOverlay.offsetWidth; // force reflow so the animation restarts cleanly
        // Wipe animates clip-path only (no opacity keyframes) — force the overlay visible
        if (transition === 'wipe') testOverlay.style.opacity = '1';
        testOverlay.classList.add(inClass);

        testCleanupTimer = setTimeout(() => {
            cleanAnimationClasses(testOverlay);
            testOverlay.style.opacity = '';
        }, 500);
    }

    // Ticker Publication
    function publishTicker(config, items) {
        if (!config || !items || items.length === 0) return;

        // Ticker styling
        if (config.isBreakingStyle) {
            newsTicker.classList.add('ticker-breaking');
            tickerBadgeText.textContent = config.headerTag || 'BREAKING';
        } else {
            newsTicker.classList.remove('ticker-breaking');
            tickerBadgeText.textContent = config.headerTag || 'HEADLINES';
        }

        if (config.ribbonHeight) {
            newsTicker.style.height = `${config.ribbonHeight}px`;
        }

        if (config.isPaused) {
            newsTicker.classList.add('ticker-paused');
        } else {
            newsTicker.classList.remove('ticker-paused');
        }

        // Build continuous double-track items for seamless loop
        const separator = escapeHtml(config.separator || '●');
        let itemsHtml = '';
        items.forEach(item => {
            itemsHtml += `
                <div class="ticker-item">
                    ${item.category ? `<span class="ticker-item-category">[${escapeHtml(item.category)}]</span>` : ''}
                    <span class="ticker-item-text">${escapeHtml(item.text)}</span>
                </div>
                <div class="ticker-separator">${separator}</div>
            `;
        });

        // Duplicate for seamless 0-gap loop
        tickerTrack.innerHTML = itemsHtml + itemsHtml;

        // Calculate smooth duration based on content width & speed (e.g. 90px/s)
        requestAnimationFrame(() => {
            const trackWidth = tickerTrack.scrollWidth / 2;
            const speed = config.speedPixelsPerSecond || 90;
            const durationSec = Math.max(10, trackWidth / speed);
            tickerTrack.style.animationDuration = `${durationSec}s`;
        });
    }

    // Lower Thirds / Headline Publication
    function publishHeadline(headline) {
        if (!headline) {
            lowerThird.classList.add('hidden');
            return;
        }

        ltTitle.textContent = headline.title || '';
        ltSubtitle.textContent = headline.subtitle || '';
        ltCategory.textContent = (headline.category || 'NEWS').toUpperCase();

        if (headline.isBreaking) {
            ltBreakingTag.classList.remove('hidden');
        } else {
            ltBreakingTag.classList.add('hidden');
        }

        if (headline.location) {
            ltLocation.textContent = headline.location.toUpperCase();
            ltLocation.classList.remove('hidden');
        } else {
            ltLocation.classList.add('hidden');
        }

        if (headline.reporterName) {
            ltReporterName.textContent = headline.reporterName;
            ltReporterTitle.textContent = headline.reporterTitle || 'Correspondent';
            ltReporterRow.classList.remove('hidden');
        } else {
            ltReporterRow.classList.add('hidden');
        }

        lowerThird.classList.remove('hidden');
    }

    function clearHeadline() {
        lowerThird.classList.add('hidden');
    }

    function toggleStandby(active) {
        isStandby = active;
        if (active) {
            layerStandby.classList.remove('hidden');
            const vid = getActiveVideo();
            if (vid && vid.src) vid.pause();
        } else {
            layerStandby.classList.add('hidden');
        }
    }

    function configureLiveBadge(visible) {
        if (visible) {
            liveIndicator.classList.remove('hidden');
        } else {
            liveIndicator.classList.add('hidden');
        }
    }

    // Message Dispatcher from WPF Host
    function handleHostMessage(msg) {
        if (!msg || !msg.command) return;

        switch (msg.command) {
            case 'load_scene':
                loadScene(msg.data);
                break;
            case 'take_live':
                takeLive(msg.data);
                break;
            case 'test_transition':
                testTransition(msg.data);
                break;
            case 'play':
                play();
                break;
            case 'pause':
                pause();
                break;
            case 'resume':
                play();
                break;
            case 'stop':
                stop();
                break;
            case 'replay':
                seek(0);
                play();
                break;
            case 'seek':
                seek(Number(msg.data));
                break;
            case 'set_volume':
                setVolume(Number(msg.data));
                break;
            case 'publish_headline':
                publishHeadline(msg.data);
                break;
            case 'clear_headline':
                clearHeadline();
                break;
            case 'publish_ticker':
                publishTicker(msg.data.config, msg.data.items);
                break;
            case 'toggle_standby':
                toggleStandby(Boolean(msg.data));
                break;
            case 'configure_live_badge':
                configureLiveBadge(Boolean(msg.data));
                break;
        }
    }

    // Register WebView2 listener
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.addEventListener('message', event => {
            handleHostMessage(event.data);
        });
        sendHostMessage('ready', { status: 'canvas_loaded' });
    }

    // Expose for testing/debugging
    window.NewsDeskCanvas = {
        handleHostMessage,
        loadScene,
        takeLive,
        testTransition,
        publishTicker,
        publishHeadline,
        toggleStandby
    };

})();
