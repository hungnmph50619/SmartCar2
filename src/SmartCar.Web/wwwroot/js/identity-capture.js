(() => {
    document.addEventListener('DOMContentLoaded', () => {
        document.querySelectorAll('[data-identity-capture-widget]').forEach(initializeWidget);
        const mobilePage = document.querySelector('[data-identity-mobile-page]');
        if (mobilePage) initializeMobilePage(mobilePage);
    });

    function csrfToken() {
        return document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
    }

    async function fetchJson(url, options = {}) {
        const headers = new Headers(options.headers || {});
        const token = csrfToken();
        if (token) headers.set('RequestVerificationToken', token);
        const response = await fetch(url, { ...options, headers });
        const contentType = response.headers.get('content-type') || '';
        const payload = contentType.includes('application/json') ? await response.json() : null;
        if (!response.ok) {
            throw new Error(payload?.error || `Yêu cầu thất bại (${response.status}).`);
        }
        return payload;
    }

    function initializeWidget(root) {
        const purpose = root.dataset.purpose;
        const bookingId = root.dataset.bookingId || '';
        const customerId = root.dataset.customerId || '';
        const createUrl = root.dataset.createUrl;
        const submitUrl = root.dataset.submitUrl;
        const statusUrl = root.dataset.statusUrl;
        const fallbackUrl = root.dataset.fallbackUrl;
        const hidden = root.querySelector('[data-face-session]');
        const status = root.querySelector('[data-face-status]');
        const preview = root.querySelector('[data-face-preview]');
        const video = root.querySelector('[data-face-video]');
        const cameraPanel = root.querySelector('[data-camera-panel]');
        const handoffPanel = root.querySelector('[data-handoff-panel]');
        const qr = root.querySelector('[data-handoff-qr]');
        const handoffLink = root.querySelector('[data-handoff-link]');
        const startCamera = root.querySelector('[data-start-camera]');
        const takePhoto = root.querySelector('[data-take-photo]');
        const usePhone = root.querySelector('[data-use-phone]');
        const cancelCamera = root.querySelector('[data-cancel-camera]');
        const fallbackForm = root.querySelector('[data-staff-fallback-form]');
        let session = null;
        let stream = null;
        let pollTimer = null;

        const showMessage = (message, kind = 'muted') => {
            if (!status) return;
            status.textContent = message;
            status.className = `small mt-2 text-${kind}`;
        };

        const stopCamera = () => {
            stream?.getTracks().forEach(track => track.stop());
            stream = null;
            if (video) video.srcObject = null;
        };

        const complete = (sessionId, imageUrl, captureMethod) => {
            if (hidden) hidden.value = sessionId;
            if (preview && imageUrl) {
                preview.src = imageUrl;
                preview.classList.remove('d-none');
            }
            cameraPanel?.classList.add('d-none');
            handoffPanel?.classList.add('d-none');
            stopCamera();
            if (pollTimer) window.clearInterval(pollTimer);
            showMessage(
                captureMethod === 'StaffFallbackUpload'
                    ? '✓ Đã có ảnh xác minh do Staff tải dự phòng (được ghi audit).'
                    : '✓ Đã chụp ảnh xác minh trực tiếp.',
                'success');
        };

        const createSession = async () => {
            if (session) return session;
            const body = new URLSearchParams({ purpose });
            if (bookingId) body.set('bookingId', bookingId);
            if (customerId) body.set('customerId', customerId);
            session = await fetchJson(createUrl, {
                method: 'POST',
                headers: { 'Content-Type': 'application/x-www-form-urlencoded;charset=UTF-8' },
                body: body.toString()
            });
            return session;
        };

        const startLocalCamera = async () => {
            try {
                await createSession();
                if (!navigator.mediaDevices?.getUserMedia) {
                    throw new Error('Thiết bị/trình duyệt này không hỗ trợ camera trực tiếp. Hãy dùng QR để chụp bằng điện thoại.');
                }
                stream = await navigator.mediaDevices.getUserMedia({
                    video: { facingMode: 'user', width: { ideal: 1280 }, height: { ideal: 720 } },
                    audio: false
                });
                if (video) {
                    video.srcObject = stream;
                    await video.play();
                }
                cameraPanel?.classList.remove('d-none');
                handoffPanel?.classList.add('d-none');
                showMessage('Đặt khuôn mặt rõ, đủ sáng, nhìn thẳng camera.');
            } catch (error) {
                stopCamera();
                showMessage(error.message || 'Không mở được camera.', 'danger');
            }
        };

        const captureFrame = async () => {
            if (!session || !video || !video.videoWidth) {
                showMessage('Camera chưa sẵn sàng.', 'danger');
                return;
            }
            const canvas = document.createElement('canvas');
            canvas.width = video.videoWidth;
            canvas.height = video.videoHeight;
            canvas.getContext('2d').drawImage(video, 0, 0, canvas.width, canvas.height);
            const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/jpeg', 0.9));
            if (!blob) {
                showMessage('Không tạo được ảnh từ camera.', 'danger');
                return;
            }
            const form = new FormData();
            form.append('sessionId', session.sessionId);
            form.append('token', session.token);
            form.append('mobileHandoff', 'false');
            form.append('image', blob, 'face-camera.jpg');
            try {
                showMessage('Đang lưu ảnh xác minh...');
                const result = await fetchJson(submitUrl, { method: 'POST', body: form });
                complete(result.sessionId, result.imageUrl, 'Camera');
            } catch (error) {
                showMessage(error.message || 'Không lưu được ảnh.', 'danger');
            }
        };

        const startHandoff = async () => {
            try {
                const current = await createSession();
                if (qr) qr.src = current.qrUrl;
                if (handoffLink) {
                    handoffLink.href = current.captureUrl;
                    handoffLink.textContent = 'Mở trang chụp trên thiết bị khác';
                }
                handoffPanel?.classList.remove('d-none');
                cameraPanel?.classList.add('d-none');
                stopCamera();
                showMessage('Quét QR bằng điện thoại. Ảnh sẽ tự quay lại màn hình này sau khi chụp.');
                if (pollTimer) window.clearInterval(pollTimer);
                pollTimer = window.setInterval(async () => {
                    try {
                        const state = await fetchJson(`${statusUrl}?sessionId=${encodeURIComponent(current.sessionId)}`);
                        if (state.completed) {
                            complete(state.sessionId, state.imageUrl, state.captureMethod);
                        } else if (state.expired) {
                            window.clearInterval(pollTimer);
                            session = null;
                            showMessage('Phiên QR đã hết hạn. Hãy tạo lại.', 'danger');
                        }
                    } catch {
                        // transient polling failure: keep trying until session expires
                    }
                }, 2000);
            } catch (error) {
                showMessage(error.message || 'Không tạo được phiên chụp bằng điện thoại.', 'danger');
            }
        };

        startCamera?.addEventListener('click', startLocalCamera);
        takePhoto?.addEventListener('click', captureFrame);
        usePhone?.addEventListener('click', startHandoff);
        cancelCamera?.addEventListener('click', () => {
            stopCamera();
            cameraPanel?.classList.add('d-none');
        });

        fallbackForm?.addEventListener('submit', async event => {
            event.preventDefault();
            if (!fallbackUrl) return;
            const form = new FormData(fallbackForm);
            form.set('purpose', purpose);
            if (bookingId) form.set('bookingId', bookingId);
            if (customerId) form.set('customerId', customerId);
            try {
                showMessage('Đang lưu ảnh dự phòng và ghi audit...');
                const result = await fetchJson(fallbackUrl, { method: 'POST', body: form });
                complete(result.sessionId, result.imageUrl, 'StaffFallbackUpload');
            } catch (error) {
                showMessage(error.message || 'Không lưu được ảnh dự phòng.', 'danger');
            }
        });

        const existingSessionId = hidden?.value;
        if (existingSessionId) {
            fetchJson(`${statusUrl}?sessionId=${encodeURIComponent(existingSessionId)}`)
                .then(state => {
                    if (state.completed) complete(state.sessionId, state.imageUrl, state.captureMethod);
                })
                .catch(() => {});
        }
    }

    function initializeMobilePage(root) {
        const sessionId = root.dataset.sessionId;
        const token = root.dataset.token;
        const submitUrl = root.dataset.submitUrl;
        const video = root.querySelector('[data-mobile-video]');
        const status = root.querySelector('[data-mobile-status]');
        const startButton = root.querySelector('[data-mobile-start]');
        const captureButton = root.querySelector('[data-mobile-capture]');
        const preview = root.querySelector('[data-mobile-preview]');
        let stream = null;

        const message = (text, danger = false) => {
            if (!status) return;
            status.textContent = text;
            status.className = danger ? 'alert alert-danger' : 'alert alert-info';
        };

        const stop = () => {
            stream?.getTracks().forEach(track => track.stop());
            stream = null;
        };

        startButton?.addEventListener('click', async () => {
            try {
                if (!navigator.mediaDevices?.getUserMedia) {
                    throw new Error('Trình duyệt này không hỗ trợ chụp camera. Hãy dùng thiết bị khác hoặc nhờ Staff hỗ trợ tại quầy.');
                }
                stream = await navigator.mediaDevices.getUserMedia({
                    video: { facingMode: 'user', width: { ideal: 1280 }, height: { ideal: 720 } },
                    audio: false
                });
                video.srcObject = stream;
                await video.play();
                video.classList.remove('d-none');
                captureButton.classList.remove('d-none');
                message('Đặt mặt rõ, đủ sáng và nhìn thẳng camera.');
            } catch (error) {
                message(error.message || 'Không mở được camera.', true);
            }
        });

        captureButton?.addEventListener('click', async () => {
            if (!stream || !video.videoWidth) return;
            const canvas = document.createElement('canvas');
            canvas.width = video.videoWidth;
            canvas.height = video.videoHeight;
            canvas.getContext('2d').drawImage(video, 0, 0, canvas.width, canvas.height);
            const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/jpeg', 0.9));
            if (!blob) return;

            const form = new FormData();
            form.append('sessionId', sessionId);
            form.append('token', token);
            form.append('mobileHandoff', 'true');
            form.append('image', blob, 'face-mobile-camera.jpg');
            try {
                message('Đang gửi ảnh xác minh...');
                await fetchJson(submitUrl, { method: 'POST', body: form });
                stop();
                video.classList.add('d-none');
                captureButton.classList.add('d-none');
                if (preview) {
                    preview.src = URL.createObjectURL(blob);
                    preview.classList.remove('d-none');
                }
                message('✓ Đã chụp thành công. Có thể quay lại máy tính; màn hình xử lý sẽ tự cập nhật.');
                startButton.classList.add('d-none');
            } catch (error) {
                message(error.message || 'Không gửi được ảnh.', true);
            }
        });

        window.addEventListener('beforeunload', stop, { once: true });
    }
})();
