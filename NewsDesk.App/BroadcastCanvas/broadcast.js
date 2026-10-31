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
    const imageA = document.getElementById('image-a');
    const imageB = document.getElementById('image-b');
    const captionA = document.getElementById('caption-a');
    const captionB = document.getElementById('caption-b');

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
    const layerIntro = document.getElementById('layer-intro');
    const introVideo = document.getElementById('intro-video');
    const introAudio = document.getElementById('intro-audio');
    const introMotion = document.getElementById('intro-motion');
    const standbyClock = document.getElementById('standby-clock');
    const stationChip = document.getElementById('station-chip');
    const onairClock = document.getElementById('onair-clock');
    const testOverlay = document.getElementById('test-overlay');

    // Fit the fixed 1920x1080 stage to whatever display the window is on
    const stage = document.getElementById('broadcast-container');
    function fitStage() {
        const scale = Math.min(window.innerWidth / 1920, window.innerHeight / 1080);
        stage.style.setProperty('--stage-scale', String(scale));
    }
    window.addEventListener('resize', fitStage);
    fitStage();

    // Station clock (shared by the on-air chip and the standby slate)
    function updateClock() {
        const now = new Date();
        const hrs = String(now.getHours()).padStart(2, '0');
        const mins = String(now.getMinutes()).padStart(2, '0');
        const secs = String(now.getSeconds()).padStart(2, '0');
        const timeText = `${hrs}:${mins}:${secs}`;
        if (standbyClock) {
            standbyClock.textContent = timeText;
        }
        if (onairClock) {
            onairClock.textContent = timeText;
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

    function getStandbyImage() {
        return activeSlot === 'a' ? imageB : imageA;
    }

    function getStandbyCaption() {
        return activeSlot === 'a' ? captionB : captionA;
    }

    // Name + designation strap for a person in the frame; hidden when no name is given
    function setSceneCaption(captionEl, name, title) {
        const hasCaption = Boolean(name && String(name).trim());
        captionEl.querySelector('.caption-name').textContent = hasCaption ? name : '';
        captionEl.querySelector('.caption-title').textContent = hasCaption ? (title || '') : '';
        captionEl.classList.toggle('hidden', !hasCaption);
    }

    function clearImage(img) {
        img.style.display = 'none';
        img.onload = null;
        img.onerror = null;
        img.removeAttribute('src');
    }

    // Prepare content in standby slot without touching active broadcast.
    // A load arriving DURING a transition must not touch the incoming scene:
    // it is queued and applied once the flip completes.
    let pendingLoadData = null;
    let loadGeneration = 0;
    let scenePreparedTimer = null;
    let introTimer = null;
    let introPostAction = 'opening_headline';
    introVideo.addEventListener('ended', beginIntroExit);
    function loadScene(data) {
        if (isTransitioning) {
            pendingLoadData = data;
            return;
        }

        const generation = ++loadGeneration;

        const standbyVid = getStandbyVideo();
        const standbyGraph = getStandbyGraphic();
        const standbyImg = getStandbyImage();
        const standbySlot = activeSlot === 'a' ? 'b' : 'a';

        let preparedSent = false;
        let loadFailed = false;
        const markPrepared = () => {
            if (preparedSent || loadFailed || generation !== loadGeneration) return;
            preparedSent = true;
            sendHostMessage('scene_prepared', { slot: standbySlot, generation: generation });
        };
        const reportLoadFailure = (message) => {
            if (loadFailed || generation !== loadGeneration) return;
            loadFailed = true;
            if (scenePreparedTimer !== null) {
                clearTimeout(scenePreparedTimer);
                scenePreparedTimer = null;
            }
            sendHostMessage('error', { message: message, slot: standbySlot });
        };

        // A superseded loadScene must not let its fallback timer fire later and
        // prematurely confirm the NEW load as ready
        if (scenePreparedTimer !== null) {
            clearTimeout(scenePreparedTimer);
            scenePreparedTimer = null;
        }

        setSceneCaption(getStandbyCaption(), data.captionName, data.captionTitle);

        if (data.type === 'image' && data.mediaUrl) {
            standbyVid.style.display = 'none';
            standbyVid.pause();
            standbyVid.removeAttribute('src');
            standbyVid.load();
            standbyGraph.style.display = 'none';

            // Prepared only once the picture has decoded, never a blank frame
            standbyImg.onload = () => {
                standbyImg.onload = null;
                markPrepared();
            };
            standbyImg.onerror = () => {
                standbyImg.onerror = null;
                reportLoadFailure(`Picture failed to load in slot ${standbySlot}`);
            };
            standbyImg.style.display = 'block';
            standbyImg.src = data.mediaUrl;
        } else if (data.type === 'video' && data.mediaUrl) {
            clearImage(standbyImg);
            standbyVid.style.display = 'block';
            standbyGraph.style.display = 'none';

            // Report prepared only once the media can actually render, so the host
            // never takes live a scene that would flash black mid-load.
            standbyVid.addEventListener('loadeddata', markPrepared, { once: true });
            standbyVid.addEventListener('error', () => {
                reportLoadFailure(`Preview media failed to load in slot ${standbySlot}`);
            }, { once: true });
            scenePreparedTimer = setTimeout(() => {
                reportLoadFailure(`Preview media did not become ready within 4000ms in slot ${standbySlot}`);
            }, 4000);

            standbyVid.src = data.mediaUrl;
            standbyVid.volume = 0; // muted while standing by
            standbyVid.load();
            if (data.autoPlay) {
                const startWhenReady = () => {
                    if (generation !== loadGeneration || loadFailed) return;
                    standbyVid.play().catch(() => {});
                };
                if (standbyVid.readyState >= HTMLMediaElement.HAVE_CURRENT_DATA) startWhenReady();
                else standbyVid.addEventListener('canplay', startWhenReady, { once: true });
            }
        } else if (data.type === 'graphic') {
            clearImage(standbyImg);
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

    const BRAND_HTML = `
        <div class="brand">
            <div class="brand-row">
                <span class="brand-e">E</span>
                <span class="brand-name">EMPYREAN NEWS</span>
            </div>
            <div class="brand-edition">THE FAREWELL EDITION</div>
        </div>`;

    function renderGraphicSlide(container, graphic) {
        const title = graphic.title || 'HEADLINE';
        const subtitle = graphic.subtitle || '';
        const category = graphic.category || 'SPECIAL REPORT';
        const bg = String(graphic.backgroundStyle || 'navy').toLowerCase();
        const isBreaking = Boolean(graphic.isBreaking) || bg === 'breaking';

        // Studio variants: navy = standard light studio, breaking = pulsing red tag, slate = cooler studio
        const variantClass = isBreaking ? 'slide-breaking' : (bg === 'slate' ? 'slide-slate' : '');

        container.innerHTML = `
            <div class="fullscreen-slide ${variantClass}">
                <div class="studio-bg"></div>
                <div class="studio-globe"></div>
                <div class="studio-shine"></div>
                ${BRAND_HTML}
                <div class="slide-copy">
                    <div class="slide-badge">${escapeHtml(category)}</div>
                    <h1 class="slide-title">${escapeHtml(title)}</h1>
                    ${subtitle ? `<p class="slide-subtitle">${escapeHtml(subtitle)}</p>` : ''}
                </div>
                <div class="studio-floor"></div>
            </div>
        `;

        fitSlideTitle(container.querySelector('.slide-title'));
    }

    // Long headlines shrink until they fit in three lines above the lower-third zone
    function fitSlideTitle(titleEl) {
        if (!titleEl) return;
        let size = 104;
        titleEl.style.fontSize = `${size}px`;
        while (size > 60 && titleEl.scrollHeight > size * 0.98 * 3 + 4) {
            size -= 6;
            titleEl.style.fontSize = `${size}px`;
        }
    }

    // Audio Crossfader (linear volume ramp over transitionDurationMs)
    function crossfadeAudio(outgoingVid, incomingVid, durationMs) {
        const target = programTargetVolume();
        if (durationMs <= 0) {
            if (outgoingVid) outgoingVid.volume = 0;
            if (incomingVid) incomingVid.volume = target;
            return;
        }
        const outgoingStart = outgoingVid ? outgoingVid.volume : 0;

        const stepInterval = 25; // 25ms steps
        const totalSteps = durationMs / stepInterval;
        let step = 0;

        if (incomingVid) incomingVid.volume = 0;

        const interval = setInterval(() => {
            step++;
            const progress = Math.min(1, step / totalSteps);

            if (incomingVid) {
                incomingVid.volume = clamp01(target * progress);
            }
            if (outgoingVid) {
                outgoingVid.volume = clamp01(outgoingStart * (1 - progress));
            }

            if (step >= totalSteps) {
                clearInterval(interval);
                if (outgoingVid) outgoingVid.volume = 0;
                if (incomingVid) incomingVid.volume = target;
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

        // Hide the compact station bug while a full-brand slide is on air
        document.body.classList.toggle('on-graphic', getStandbyGraphic().style.display === 'flex');

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

            // Apply any cue that waited out the transition (now targets the new standby slot)
            if (pendingLoadData) {
                const queued = pendingLoadData;
                pendingLoadData = null;
                loadScene(queued);
            }

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

            // Apply any cue that waited out the transition (now targets the new standby slot)
            if (pendingLoadData) {
                const queued = pendingLoadData;
                pendingLoadData = null;
                loadScene(queued);
            }

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

    function clamp01(v) {
        v = Number(v);
        return Number.isFinite(v) ? Math.max(0, Math.min(1, v)) : 0;
    }

    // ------------------------------------------------------------------
    // PROGRAM AUDIO: mixer level, ducked while a voice-over or a speaker
    // window with sound is on air.
    // ------------------------------------------------------------------
    let programVolume = 1;
    let duckLevel = 0.25;
    let voActive = false;
    let duckRampFrame = null;

    function isProgramDucked() {
        return voActive || speakerBoxes.some(b => b.playAudio && !b.el.classList.contains('hidden'));
    }

    function programTargetVolume() {
        return clamp01(programVolume * (isProgramDucked() ? duckLevel : 1));
    }

    function applyProgramVolume(rampMs) {
        if (isTransitioning) return; // the crossfade lands on the new target itself
        const vid = getActiveVideo();
        if (!vid) return;
        const from = vid.volume;
        const to = programTargetVolume();
        // A timer, not requestAnimationFrame: rAF stops while the window is hidden or
        // covered, which would leave the program un-ducked under a voice-over.
        if (duckRampFrame !== null) clearInterval(duckRampFrame);
        duckRampFrame = null;
        const startedAt = performance.now();
        const step = () => {
            const progress = rampMs > 0 ? Math.min(1, (performance.now() - startedAt) / rampMs) : 1;
            vid.volume = clamp01(from + (to - from) * progress);
            if (progress >= 1 && duckRampFrame !== null) {
                clearInterval(duckRampFrame);
                duckRampFrame = null;
            }
            return progress;
        };
        if (step() < 1) duckRampFrame = setInterval(step, 20);
    }

    function setVolume(val) {
        programVolume = clamp01(val);
        applyProgramVolume(150);
    }

    // ------------------------------------------------------------------
    // VOICE-OVER: narration over the program. With an audio file it plays
    // that track; without one it is a "live narrator" VO (someone speaking
    // in the room) that just ducks the program and shows the tag.
    // ------------------------------------------------------------------
    const voAudio = document.getElementById('vo-audio');
    const voTag = document.getElementById('vo-tag');
    const voName = document.getElementById('vo-name');
    const voTitle = document.getElementById('vo-title');

    voAudio.addEventListener('ended', () => {
        if (!voAudio.loop) stopVoiceOver(true);
    });

    function startVoiceOver(data) {
        data = data || {};
        stopVoiceOver(false);
        voActive = true;
        duckLevel = clamp01(data.duckLevel ?? 0.25);

        voName.textContent = data.name || '';
        voTitle.textContent = data.name ? (data.designation || '') : '';
        voTag.classList.toggle('hidden', data.showTag === false);

        if (data.audioUrl) {
            voAudio.src = data.audioUrl;
            voAudio.loop = Boolean(data.loop);
            voAudio.volume = clamp01(data.volume ?? 1);
            voAudio.load();
            if (data.startAt > 0) voAudio.currentTime = data.startAt;
            voAudio.play().catch(() => {
                sendHostMessage('overlay_error', { message: 'Voice-over audio could not start.' });
                stopVoiceOver(true);
            });
        }
        applyProgramVolume(data.rampMs ?? 400);
        sendHostMessage('vo_started', { hasAudio: Boolean(data.audioUrl) });
    }

    // The clip on air keeps talking as a voice-over: its soundtrack moves to the
    // VO player (picking up where it is) and the program goes silent, so the
    // operator can take pictures or other video live over the interview audio.
    function startVoiceOverFromProgram(data) {
        const vid = getActiveVideo();
        if (!vid || !vid.currentSrc || vid.style.display === 'none' || vid.ended) {
            sendHostMessage('overlay_error', { message: 'No video is on air to take the voice-over sound from.' });
            sendHostMessage('vo_ended', {});
            return;
        }

        startVoiceOver(Object.assign({}, data, {
            audioUrl: vid.currentSrc,
            startAt: vid.currentTime,
            duckLevel: 0,      // program fully silent: only the held soundtrack is heard
            loop: false,
            rampMs: 120        // near-instant swap, so the two copies never echo
        }));

        // Lip sync while the same clip is still on screen: one hard re-align once the VO
        // player runs, then small speed nudges until the operator cuts away.
        const src = voAudio.src;
        voAudio.addEventListener('playing', () => {
            if (voActive && getActiveVideo() === vid && !vid.paused) voAudio.currentTime = vid.currentTime;
        }, { once: true });

        const sync = setInterval(() => {
            const sameClipOnAir = voActive && voAudio.src === src && getActiveVideo() === vid && !vid.paused && !isTransitioning;
            if (!sameClipOnAir) {
                voAudio.playbackRate = 1;
                if (!voActive || voAudio.src !== src || getActiveVideo() !== vid) clearInterval(sync);
                return;
            }
            const drift = voAudio.currentTime - vid.currentTime; // + = sound ahead of picture
            if (Math.abs(drift) > 0.5) voAudio.currentTime = vid.currentTime;
            else voAudio.playbackRate = Math.abs(drift) < 0.03 ? 1 : (drift > 0 ? 0.96 : 1.04);
        }, 250);
    }

    function stopVoiceOver(notify) {
        const wasActive = voActive;
        voActive = false;
        voAudio.pause();
        voAudio.removeAttribute('src');
        voAudio.load();
        voTag.classList.add('hidden');
        applyProgramVolume(600);
        if (notify && wasActive) sendHostMessage('vo_ended', {});
    }

    function setVoiceOverLevels(data) {
        if (!data) return;
        if (data.volume !== undefined) voAudio.volume = clamp01(data.volume);
        if (data.duckLevel !== undefined) {
            duckLevel = clamp01(data.duckLevel);
            applyProgramVolume(200);
        }
    }

    // ------------------------------------------------------------------
    // SPEAKER WINDOWS: two floating boxes (video clip, picture or webcam)
    // with a name + designation caption each.
    // ------------------------------------------------------------------
    const speakerLayer = document.getElementById('layer-speakers');
    const speakerBoxes = [1, 2].map(n => ({
        el: document.getElementById(`speaker-${n}`),
        video: document.getElementById(`speaker-video-${n}`),
        img: document.getElementById(`speaker-img-${n}`),
        name: document.getElementById(`speaker-name-${n}`),
        title: document.getElementById(`speaker-title-${n}`),
        key: null,
        stream: null,
        playAudio: false
    }));

    function resetSpeakerMedia(box) {
        box.key = null;
        if (box.stream) {
            box.stream.getTracks().forEach(t => t.stop());
            box.stream = null;
        }
        box.video.pause();
        box.video.srcObject = null;
        box.video.removeAttribute('src');
        box.video.load();
        box.video.style.display = 'none';
        box.img.removeAttribute('src');
        box.img.style.display = 'none';
    }

    async function attachCamera(box, index, key) {
        if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
            sendHostMessage('overlay_error', { message: 'Cameras are not available on this output.' });
            return;
        }
        try {
            let stream = await navigator.mediaDevices.getUserMedia({ video: true, audio: false });
            const cams = (await navigator.mediaDevices.enumerateDevices()).filter(d => d.kind === 'videoinput');
            const wanted = cams[index] || cams[0];
            const track = stream.getVideoTracks()[0];
            const currentId = track && track.getSettings ? track.getSettings().deviceId : '';
            if (wanted && wanted.deviceId && wanted.deviceId !== currentId) {
                stream.getTracks().forEach(t => t.stop());
                stream = await navigator.mediaDevices.getUserMedia({
                    video: { deviceId: { exact: wanted.deviceId }, width: { ideal: 1280 }, height: { ideal: 720 } },
                    audio: false
                });
            }
            if (box.key !== key) { // superseded while the camera was opening
                stream.getTracks().forEach(t => t.stop());
                return;
            }
            if (!cams[index] && index > 0) {
                sendHostMessage('overlay_error', { message: `Camera ${index + 1} not found — showing camera 1 instead.` });
            }
            box.stream = stream;
            box.video.srcObject = stream;
            box.video.muted = true;
            box.video.style.display = 'block';
            box.video.play().catch(() => {});
        } catch (e) {
            sendHostMessage('overlay_error', { message: `Camera ${index + 1} could not start (${e && e.name ? e.name : e}).` });
        }
    }

    function showSpeakers(data) {
        data = data || {};
        const configs = data.boxes || [];
        speakerLayer.dataset.layout = data.layout === 'pip_right' ? 'pip_right' : 'side_by_side';

        let visibleCount = 0;
        speakerBoxes.forEach((box, i) => {
            const cfg = configs[i] || {};
            const visible = Boolean(cfg.visible) && Boolean(cfg.kind) && cfg.kind !== 'none';
            box.el.classList.toggle('hidden', !visible);
            if (!visible) {
                resetSpeakerMedia(box);
                box.playAudio = false;
                return;
            }
            visibleCount++;

            box.name.textContent = cfg.name || '';
            box.title.textContent = cfg.designation || '';
            box.el.classList.toggle('no-caption', !cfg.name && !cfg.designation);
            box.playAudio = cfg.kind === 'video' && Boolean(cfg.playAudio);

            // Re-sending an unchanged source only updates captions/audio, never restarts media
            const key = cfg.kind === 'camera' ? `camera:${cfg.cameraIndex}` : `${cfg.kind}:${cfg.url}`;
            if (key !== box.key) {
                resetSpeakerMedia(box);
                box.key = key;
                if (cfg.kind === 'camera') {
                    attachCamera(box, Number(cfg.cameraIndex) || 0, key);
                } else if (cfg.kind === 'image') {
                    box.img.src = cfg.url;
                    box.img.style.display = 'block';
                } else if (cfg.kind === 'video') {
                    box.video.src = cfg.url;
                    box.video.style.display = 'block';
                    box.video.load();
                }
            }
            if (cfg.kind === 'video') {
                box.video.muted = !box.playAudio;
                box.video.volume = 1;
                if (!isStandby) box.video.play().catch(() => {});
            }
        });

        speakerLayer.classList.toggle('single', visibleCount === 1);
        speakerLayer.classList.toggle('hidden', visibleCount === 0);
        // Big side-by-side windows cover the scene, so its own caption steps aside
        document.body.classList.toggle('speakers-full', visibleCount > 0 && speakerLayer.dataset.layout === 'side_by_side');
        applyProgramVolume(400);
    }

    function hideSpeakers() {
        speakerBoxes.forEach(box => {
            resetSpeakerMedia(box);
            box.playAudio = false;
            box.el.classList.add('hidden');
        });
        speakerLayer.classList.add('hidden');
        document.body.classList.remove('speakers-full');
        applyProgramVolume(400);
    }

    // ------------------------------------------------------------------
    // INTRO
    // The built-in sequence runs ~12s (timings live in broadcast.css). The
    // host is told "intro_ended" when the closing fade STARTS, so it can take
    // the opening scene live underneath while the intro dissolves away.
    // ------------------------------------------------------------------
    const INTRO_BUILTIN_MS = 12000;
    const INTRO_FADE_MS = 700;
    let introEnding = false;
    let introCleanupTimer = null;

    function clearIntroTimers() {
        if (introTimer !== null) { clearTimeout(introTimer); introTimer = null; }
        if (introCleanupTimer !== null) { clearTimeout(introCleanupTimer); introCleanupTimer = null; }
    }

    function resetIntroMedia() {
        introVideo.pause();
        introAudio.pause();
        introVideo.removeAttribute('src');
        introAudio.removeAttribute('src');
        introVideo.load();
        introAudio.load();
    }

    function playIntro(data) {
        if (!layerIntro) return;
        data = data || {};
        introPostAction = data.postIntroAction || 'opening_headline';
        const volume = Math.max(0, Math.min(1, Number(data.volume ?? 1)));

        // Restart cleanly even if an intro is already running (no overlapping audio)
        clearIntroTimers();
        stopThemeAudio(0);
        resetIntroMedia();
        introEnding = false;
        layerIntro.classList.add('hidden');
        layerIntro.classList.remove('intro-out', 'intro-run');
        void layerIntro.offsetWidth; // display:none → block restarts every CSS animation

        const builtIn = !data.videoUrl;
        introMotion.classList.toggle('hidden', !builtIn);
        layerIntro.classList.remove('hidden');
        layerIntro.setAttribute('aria-hidden', 'false');

        if (data.videoUrl) {
            introVideo.src = data.videoUrl;
            introVideo.muted = Boolean(data.audioUrl);
            introVideo.volume = volume;
            introVideo.load();
            introVideo.play().catch(() => reportIntroError('Intro video could not start.'));
        }

        if (data.audioUrl) {
            introAudio.src = data.audioUrl;
            introAudio.volume = volume;
            introAudio.load();
            introAudio.play().catch(() => reportIntroError('Intro audio could not start.'));
        } else if (builtIn && data.useBuiltInMotion !== false) {
            startThemeAudio(volume);
        }

        if (builtIn) {
            layerIntro.classList.add('intro-run');
            const total = Number(data.durationMs) || INTRO_BUILTIN_MS;
            introTimer = setTimeout(beginIntroExit, Math.max(1000, total - INTRO_FADE_MS));
        }

        sendHostMessage('intro_started', { builtIn: builtIn });
    }

    function reportIntroError(message) {
        sendHostMessage('intro_error', { message: message });
        beginIntroExit();
    }

    function beginIntroExit() {
        if (!layerIntro || layerIntro.classList.contains('hidden') || introEnding) return;
        introEnding = true;
        clearIntroTimers();
        sendHostMessage('intro_ended', { postIntroAction: introPostAction });

        layerIntro.classList.add('intro-out');
        stopThemeAudio(INTRO_FADE_MS);
        fadeMediaOut(introAudio, INTRO_FADE_MS);
        fadeMediaOut(introVideo, INTRO_FADE_MS);

        introCleanupTimer = setTimeout(() => {
            introCleanupTimer = null;
            resetIntroMedia();
            layerIntro.classList.add('hidden');
            layerIntro.classList.remove('intro-out', 'intro-run');
            layerIntro.setAttribute('aria-hidden', 'true');
            introEnding = false;
        }, INTRO_FADE_MS);
    }

    function fadeMediaOut(media, durationMs) {
        const startVolume = media.volume;
        const startedAt = performance.now();
        const step = () => {
            const progress = Math.min(1, (performance.now() - startedAt) / durationMs);
            media.volume = startVolume * (1 - progress);
            if (progress < 1) requestAnimationFrame(step);
        };
        step();
    }

    function skipIntro() {
        beginIntroExit();
    }

    // ------------------------------------------------------------------
    // BUILT-IN THEME: an original news sting synthesised live with WebAudio,
    // scored to the intro timeline — countdown pips and drums (0–6s), a riser,
    // the brand "impact" chord at 6.3s, a second hit at 8.4s, then ring-out.
    // ------------------------------------------------------------------
    let themeCtx = null;
    let themeMaster = null;

    function startThemeAudio(volume) {
        stopThemeAudio(0);
        const Ctx = window.AudioContext || window.webkitAudioContext;
        if (!Ctx) return;

        const ctx = new Ctx();
        ctx.resume().catch(() => {});
        themeCtx = ctx;

        const master = ctx.createGain();
        master.gain.value = volume;
        master.connect(ctx.destination);
        themeMaster = master;

        // Simple space: feedback delay mixed under the dry bus
        const bus = ctx.createGain();
        const delay = ctx.createDelay(1);
        const feedback = ctx.createGain();
        const wet = ctx.createGain();
        delay.delayTime.value = 0.27;
        feedback.gain.value = 0.3;
        wet.gain.value = 0.22;
        bus.connect(master);
        bus.connect(delay);
        delay.connect(feedback);
        feedback.connect(delay);
        delay.connect(wet);
        wet.connect(master);

        const t0 = ctx.currentTime + 0.08;
        const beat = 0.5; // 120 BPM

        function tone(freq, start, dur, type, gain, attack = 0.005, cutoff = 0) {
            const osc = ctx.createOscillator();
            const env = ctx.createGain();
            osc.type = type;
            osc.frequency.value = freq;
            env.gain.setValueAtTime(0.0001, start);
            env.gain.exponentialRampToValueAtTime(gain, start + attack);
            env.gain.exponentialRampToValueAtTime(0.0001, start + dur);
            if (cutoff > 0) {
                const lp = ctx.createBiquadFilter();
                lp.type = 'lowpass';
                lp.frequency.value = cutoff;
                osc.connect(lp);
                lp.connect(env);
            } else {
                osc.connect(env);
            }
            env.connect(bus);
            osc.start(start);
            osc.stop(start + dur + 0.05);
        }

        function boom(start, gain) {
            const osc = ctx.createOscillator();
            const env = ctx.createGain();
            osc.type = 'sine';
            osc.frequency.setValueAtTime(150, start);
            osc.frequency.exponentialRampToValueAtTime(42, start + 0.35);
            env.gain.setValueAtTime(0.0001, start);
            env.gain.exponentialRampToValueAtTime(gain, start + 0.008);
            env.gain.exponentialRampToValueAtTime(0.0001, start + 0.9);
            osc.connect(env);
            env.connect(master); // drums stay dry
            osc.start(start);
            osc.stop(start + 1);
        }

        const noiseBuffer = ctx.createBuffer(1, ctx.sampleRate * 2, ctx.sampleRate);
        const noiseData = noiseBuffer.getChannelData(0);
        for (let i = 0; i < noiseData.length; i++) noiseData[i] = Math.random() * 2 - 1;

        function noise(start, dur, gain, fromHz, toHz) {
            const src = ctx.createBufferSource();
            const bp = ctx.createBiquadFilter();
            const env = ctx.createGain();
            src.buffer = noiseBuffer;
            src.loop = true;
            bp.type = 'bandpass';
            bp.Q.value = 1.2;
            bp.frequency.setValueAtTime(fromHz, start);
            bp.frequency.exponentialRampToValueAtTime(toHz, start + dur);
            env.gain.setValueAtTime(0.0001, start);
            env.gain.exponentialRampToValueAtTime(gain, start + dur * 0.92);
            env.gain.exponentialRampToValueAtTime(0.0001, start + dur + 0.08);
            src.connect(bp);
            bp.connect(env);
            env.connect(bus);
            src.start(start);
            src.stop(start + dur + 0.1);
        }

        function chord(freqs, start, dur, gain, attack, cutoff) {
            freqs.forEach(f => {
                tone(f, start, dur, 'sawtooth', gain, attack, cutoff);
                tone(f * 1.006, start, dur, 'sawtooth', gain * 0.7, attack, cutoff);
            });
        }

        // Countdown pips: steady 16ths with an accent on every beat, building up
        for (let i = 0; i < 50; i++) {
            const accent = i % 4 === 0;
            const build = 0.45 + 0.55 * (i / 50);
            tone(accent ? 1760 : 880, t0 + i * 0.125, 0.07, 'sine', (accent ? 0.13 : 0.05) * build);
        }

        // Drums on the beat, heavier on each bar
        for (let b = 0; b < 12; b++) boom(t0 + b * beat, b % 4 === 0 ? 0.9 : 0.4);

        // Pad progression under the countdown: Dm – B♭ – Gm (2s per bar)
        chord([146.83, 174.61, 220.0], t0 + 0, 2.2, 0.035, 0.5, 1400);
        chord([116.54, 146.83, 174.61], t0 + 2, 2.2, 0.035, 0.4, 1600);
        chord([98.0, 116.54, 146.83, 196.0], t0 + 4, 2.4, 0.04, 0.3, 2000);
        tone(73.42, t0, 6.3, 'sawtooth', 0.06, 1.5, 260); // low D drone

        // Riser into the brand reveal
        noise(t0 + 4.3, 2.0, 0.22, 300, 7000);

        // IMPACT at 6.3s (white flash): boom + crash + bright D-major stab
        const impact = t0 + 6.3;
        boom(impact, 1.2);
        noise(impact, 0.05, 0.5, 3000, 9000);
        chord([146.83, 220.0, 293.66, 369.99, 440.0], impact, 3.6, 0.06, 0.01, 3200);
        tone(73.42, impact, 4.5, 'sawtooth', 0.09, 0.02, 400);

        // Second hit as "SPECIAL BROADCAST" lands
        const hit2 = t0 + 8.4;
        boom(hit2, 0.8);
        chord([196.0, 246.94, 293.66, 392.0], hit2, 2.6, 0.045, 0.01, 2800);

        // Closing sting: three pips and the root
        [10.0, 10.25, 10.5].forEach(t => tone(1760, t0 + t, 0.09, 'sine', 0.12));
        chord([146.83, 220.0, 293.66], t0 + 10.75, 1.4, 0.04, 0.02, 2400);
        boom(t0 + 10.75, 0.7);
    }

    function stopThemeAudio(fadeMs) {
        if (!themeCtx) return;
        const ctx = themeCtx;
        const master = themeMaster;
        themeCtx = null;
        themeMaster = null;
        try {
            master.gain.cancelScheduledValues(ctx.currentTime);
            master.gain.setValueAtTime(master.gain.value, ctx.currentTime);
            master.gain.linearRampToValueAtTime(0, ctx.currentTime + Math.max(0.01, fadeMs / 1000));
        } catch (e) { /* context may already be closing */ }
        setTimeout(() => ctx.close().catch(() => {}), fadeMs + 100);
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
        if (!config) return;

        // Visibility (hidden ribbon keeps its dedicated position when shown again)
        if (newsTicker) {
            if (config.isVisible === false) {
                newsTicker.classList.add('hidden-ticker');
            } else {
                newsTicker.classList.remove('hidden-ticker');
            }
        }

        if (!items || items.length === 0) return;

        // Style variant (restrained red breaking ribbon vs. standard navy ribbon)
        if (config.isBreakingStyle) {
            newsTicker.classList.add('ticker-breaking');
            tickerBadgeText.textContent = config.headerTag || 'BREAKING';
        } else {
            newsTicker.classList.remove('ticker-breaking');
            tickerBadgeText.textContent = config.headerTag || 'EMPYREAN NEWS';
        }

        if (config.ribbonHeight) {
            newsTicker.style.height = `${config.ribbonHeight}px`;
        }

        // Operator-controlled colour scheme
        newsTicker.style.removeProperty('background');
        if (config.backgroundColor && !config.isBreakingStyle) {
            newsTicker.style.backgroundColor = config.backgroundColor;
        } else if (config.isBreakingStyle) {
            newsTicker.style.removeProperty('background-color');
        }

        const itemFontSize = config.fontSize ? `font-size:${config.fontSize}px;` : '';
        const itemColor = config.textColor && config.textColor.toLowerCase() !== '#ffffff' ? `color:${config.textColor};` : '';
        const itemStyle = `${itemFontSize}${itemColor}`;

        if (config.isPaused) {
            newsTicker.classList.add('ticker-paused');
        } else {
            newsTicker.classList.remove('ticker-paused');
        }

        // Build continuous double-track items for seamless loop
        const separator = escapeHtml(config.separator || '|');
        let itemsHtml = '';
        items.forEach(item => {
            itemsHtml += `
                <div class="ticker-item" ${itemStyle ? `style="${itemStyle}"` : ''}>
                    ${item.category ? `<span class="ticker-item-category">${escapeHtml(item.category)}</span>` : ''}
                    <span class="ticker-item-text">${escapeHtml(item.text)}</span>
                </div>
                <div class="ticker-separator">${separator}</div>
            `;
        });

        // Duplicate for seamless 0-gap loop
        tickerTrack.innerHTML = itemsHtml + itemsHtml;

        // Calculate smooth duration based on content width & speed (px/s)
        requestAnimationFrame(() => {
            const trackWidth = tickerTrack.scrollWidth / 2;
            const speed = config.speedPixelsPerSecond || 90;
            const durationSec = Math.max(10, trackWidth / speed);
            tickerTrack.style.animationDuration = `${durationSec}s`;
        });
    }

    // Lower Thirds / Headline Publication
    //
    // Six editorial treatments selected by the graphics-studio template type
    // (GraphicsTemplateType enum index) or explicit flags:
    //   0 MainHeadline -> lt--headline   5 ImageHeadline -> lt--headline
    //   1 BreakingNews -> lt--breaking   6 ReporterLowerThird -> lt--reporter
    //   2 TopStory     -> lt--topstory   7 IntervieweeLowerThird -> lt--reporter
    //   3 NewsUpdate   -> lt--update     4 FullScreenSlide -> lt--headline
    function resolveHeadlineTreatment(templateType) {
        switch (Number(templateType)) {
            case 1: return 'lt--breaking';
            case 2: return 'lt--topstory';
            case 3: return 'lt--update';
            case 6: return 'lt--reporter';
            case 7: return 'lt--reporter';
            default: return 'lt--headline';
        }
    }

    function publishHeadline(headline) {
        if (!headline) {
            clearHeadline();
            return;
        }

        // Select editorial treatment (breaking flag or urgent category overrides)
        const isBreaking = Boolean(headline.isBreaking) ||
            String(headline.category || '').toLowerCase() === 'breaking news';
        const treatment = isBreaking
            ? 'lt--breaking'
            : resolveHeadlineTreatment(headline.templateType);

        lowerThird.className = `lower-third-container ${treatment}`;

        ltTitle.textContent = headline.title || '';
        ltSubtitle.textContent = headline.subtitle || '';
        ltCategory.textContent = (headline.category || 'NEWS').toUpperCase();

        if (isBreaking) {
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

        // Restart entry animation cleanly on repeated publication
        lowerThird.style.animation = 'none';
        void lowerThird.offsetWidth;
        lowerThird.style.animation = '';

        lowerThird.classList.remove('hidden');
        document.body.classList.add('lt-live');
    }

    function clearHeadline() {
        lowerThird.classList.add('hidden');
        document.body.classList.remove('lt-live');
    }

    function toggleStandby(active) {
        isStandby = active;
        if (active) {
            layerStandby.classList.remove('hidden');
            const vid = getActiveVideo();
            if (vid && vid.src) vid.pause();
            // Nothing may keep talking over the slate
            stopVoiceOver(true);
            speakerBoxes.forEach(b => { if (b.video.src) b.video.pause(); });
        } else {
            layerStandby.classList.add('hidden');
            speakerBoxes.forEach(b => {
                if (b.video.src && !b.el.classList.contains('hidden')) b.video.play().catch(() => {});
            });
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
            case 'play_intro':
            case 'preview_intro':
            case 'replay_intro':
                playIntro(msg.data);
                break;
            case 'skip_intro':
                skipIntro();
                break;
            case 'set_intro_volume': {
                const v = Math.max(0, Math.min(1, Number(msg.data)));
                introAudio.volume = v;
                introVideo.volume = v;
                if (themeMaster) themeMaster.gain.value = v;
                break;
            }
            case 'start_voice_over':
                startVoiceOver(msg.data);
                break;
            case 'start_voice_over_from_program':
                startVoiceOverFromProgram(msg.data);
                break;
            case 'stop_voice_over':
                stopVoiceOver(true);
                break;
            case 'set_voice_over_levels':
                setVoiceOverLevels(msg.data);
                break;
            case 'show_speakers':
                showSpeakers(msg.data);
                break;
            case 'hide_speakers':
                hideSpeakers();
                break;
            case 'set_scene_captions':
                document.body.classList.toggle('captions-off', !msg.data);
                break;
            case 'set_cursor_visible':
                // Shown when the output shares the operator's screen (no projector)
                document.documentElement.classList.toggle('show-cursor', Boolean(msg.data));
                break;
        }
    }

    // Register WebView2 listener
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.addEventListener('message', event => {
            handleHostMessage(event.data);
        });

        // If this window has keyboard focus, operator shortcuts (Esc emergency
        // standby above all) must still reach the control room.
        const shortcutKeys = ['Escape', 'F5', 'F6', 'F7', ' '];
        document.addEventListener('keydown', (e) => {
            if (!shortcutKeys.includes(e.key) || e.repeat) return;
            e.preventDefault();
            sendHostMessage('key', { key: e.key });
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
        toggleStandby,
        playIntro,
        skipIntro,
        startVoiceOver,
        startVoiceOverFromProgram,
        stopVoiceOver,
        showSpeakers,
        hideSpeakers
    };

})();
