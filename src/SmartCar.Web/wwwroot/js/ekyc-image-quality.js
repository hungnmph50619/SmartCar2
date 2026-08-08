(() => {
    const thresholds = {
        minCardShortSide: 320,
        minCardLongSide: 520,
        minDocumentAreaRatio: 0.14,
        warnDocumentAreaRatio: 0.26,
        minSharpness: 55,
        warnSharpness: 95,
        minBrightness: 45,
        maxBrightness: 225,
        maxDarkRatio: 0.60,
        maxBrightRatio: 0.38,
        glareBrightRatio: 0.22,
        glareBlockRatio: 0.985,
        minEdgeDensity: 0.010,
        warnEdgeDensity: 0.018
    };

    const qualityCache = new Map();
    const documentCache = new Map();
    let openCvPromise = null;

    const fingerprint = file => file
        ? `${file.name}|${file.size}|${file.lastModified}|${file.type}`
        : '';

    const loadBitmap = async file => {
        if (window.createImageBitmap) return window.createImageBitmap(file);
        return new Promise((resolve, reject) => {
            const image = new Image();
            const url = URL.createObjectURL(file);
            image.onload = () => {
                URL.revokeObjectURL(url);
                resolve(image);
            };
            image.onerror = () => {
                URL.revokeObjectURL(url);
                reject(new Error('Không đọc được ảnh đã chọn.'));
            };
            image.src = url;
        });
    };

    const canvasFromBitmap = (bitmap, maxSide = 2800) => {
        const sourceWidth = bitmap.width || bitmap.naturalWidth || 0;
        const sourceHeight = bitmap.height || bitmap.naturalHeight || 0;
        const scale = Math.min(1, maxSide / Math.max(sourceWidth, sourceHeight));
        const canvas = document.createElement('canvas');
        canvas.width = Math.max(1, Math.round(sourceWidth * scale));
        canvas.height = Math.max(1, Math.round(sourceHeight * scale));
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        ctx.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
        return { canvas, sourceWidth, sourceHeight, scale };
    };

    const waitForOpenCvRuntime = async () => {
        const startedAt = Date.now();
        while (Date.now() - startedAt < 20000) {
            let candidate = window.cv;
            if (candidate && typeof candidate.then === 'function') {
                try {
                    candidate = await candidate;
                    window.cv = candidate;
                } catch {
                    candidate = null;
                }
            }
            if (candidate?.Mat && candidate?.findContours && candidate?.minAreaRect) return candidate;
            await new Promise(resolve => setTimeout(resolve, 80));
        }
        throw new Error('Không khởi tạo được OpenCV.js để tìm vùng giấy tờ.');
    };

    const ensureOpenCv = async () => {
        if (window.cv?.Mat && window.cv?.findContours) return window.cv;
        if (openCvPromise) return openCvPromise;

        openCvPromise = (async () => {
            let script = document.querySelector('script[data-smartcar-opencv]');
            if (!script) {
                script = document.createElement('script');
                script.src = 'https://docs.opencv.org/4.x/opencv.js';
                script.async = true;
                script.dataset.smartcarOpencv = 'true';
                document.head.appendChild(script);
            }
            if (!script.dataset.smartcarLoaded) {
                await new Promise((resolve, reject) => {
                    if (window.cv) {
                        resolve();
                        return;
                    }
                    script.addEventListener('load', () => {
                        script.dataset.smartcarLoaded = 'true';
                        resolve();
                    }, { once: true });
                    script.addEventListener('error', () => reject(new Error(
                        'Không tải được OpenCV.js. Hãy kiểm tra Internet rồi thử lại.'
                    )), { once: true });
                });
            }
            return waitForOpenCvRuntime();
        })().catch(error => {
            openCvPromise = null;
            throw error;
        });

        return openCvPromise;
    };

    const distance = (a, b) => Math.hypot(a.x - b.x, a.y - b.y);

    const orderPoints = points => {
        const sorted = [...points].sort((a, b) => a.y - b.y);
        const top = sorted.slice(0, 2).sort((a, b) => a.x - b.x);
        const bottom = sorted.slice(2, 4).sort((a, b) => a.x - b.x);
        return [top[0], top[1], bottom[1], bottom[0]];
    };

    const candidateScore = candidate => {
        const aspectPenalty = Math.abs(candidate.aspectRatio - 1.586);
        const largePenalty = Math.max(0, candidate.areaRatio - 0.58) * 3.2;
        const smallPenalty = Math.max(0, 0.12 - candidate.areaRatio) * 2.0;
        return candidate.rectangularity * 2.2 -
            aspectPenalty * 1.5 +
            Math.min(candidate.areaRatio, 0.40) * 0.8 -
            largePenalty -
            smallPenalty;
    };

    const scanContours = (cv, gray, sourceArea, closeSize, iterations, cannyLow, cannyHigh) => {
        const blurred = new cv.Mat();
        const edges = new cv.Mat();
        const closed = new cv.Mat();
        const kernel = cv.getStructuringElement(cv.MORPH_RECT, new cv.Size(closeSize, closeSize));
        const contours = new cv.MatVector();
        const hierarchy = new cv.Mat();
        const candidates = [];

        try {
            cv.GaussianBlur(gray, blurred, new cv.Size(5, 5), 0, 0, cv.BORDER_DEFAULT);
            cv.Canny(blurred, edges, cannyLow, cannyHigh);
            cv.morphologyEx(
                edges,
                closed,
                cv.MORPH_CLOSE,
                kernel,
                new cv.Point(-1, -1),
                iterations
            );
            cv.findContours(closed, contours, hierarchy, cv.RETR_LIST, cv.CHAIN_APPROX_SIMPLE);

            for (let index = 0; index < contours.size(); index += 1) {
                const contour = contours.get(index);
                try {
                    const contourArea = Math.abs(cv.contourArea(contour, false));
                    if (contourArea < sourceArea * 0.03) continue;

                    const rect = cv.minAreaRect(contour);
                    const rectWidth = Number(rect?.size?.width || 0);
                    const rectHeight = Number(rect?.size?.height || 0);
                    if (rectWidth <= 0 || rectHeight <= 0) continue;

                    const rectArea = rectWidth * rectHeight;
                    const areaRatio = rectArea / sourceArea;
                    const aspectRatio = Math.max(rectWidth, rectHeight) / Math.min(rectWidth, rectHeight);
                    const rectangularity = contourArea / rectArea;

                    if (areaRatio < 0.07 || areaRatio > 0.82) continue;
                    if (aspectRatio < 1.25 || aspectRatio > 1.95) continue;
                    if (rectangularity < 0.35) continue;

                    const vertices = cv.RotatedRect.points(rect).map(point => ({
                        x: Number(point.x),
                        y: Number(point.y)
                    }));

                    const candidate = {
                        vertices,
                        rectWidth,
                        rectHeight,
                        areaRatio,
                        aspectRatio,
                        rectangularity
                    };
                    candidate.score = candidateScore(candidate);
                    candidates.push(candidate);
                } finally {
                    contour.delete();
                }
            }
        } finally {
            blurred.delete();
            edges.delete();
            closed.delete();
            kernel.delete();
            contours.delete();
            hierarchy.delete();
        }

        return candidates;
    };

    const findBestDocumentCandidate = (cv, detectionCanvas) => {
        const src = cv.imread(detectionCanvas);
        const gray = new cv.Mat();
        const equalized = new cv.Mat();
        const candidates = [];
        try {
            cv.cvtColor(src, gray, cv.COLOR_RGBA2GRAY);
            cv.equalizeHist(gray, equalized);
            const sourceArea = detectionCanvas.width * detectionCanvas.height;
            const passes = [
                [gray, 9, 2, 20, 70],
                [gray, 9, 2, 30, 100],
                [gray, 5, 1, 50, 150],
                [equalized, 9, 2, 30, 100],
                [equalized, 5, 1, 50, 150]
            ];

            passes.forEach(([source, closeSize, iterations, low, high]) => {
                candidates.push(...scanContours(
                    cv,
                    source,
                    sourceArea,
                    closeSize,
                    iterations,
                    low,
                    high
                ));
            });
        } finally {
            src.delete();
            gray.delete();
            equalized.delete();
        }

        return candidates.sort((left, right) => right.score - left.score)[0] || null;
    };

    const warpDocument = (cv, sourceCanvas, orderedPoints) => {
        const [topLeft, topRight, bottomRight, bottomLeft] = orderedPoints;
        const measuredWidth = Math.max(
            distance(topLeft, topRight),
            distance(bottomLeft, bottomRight)
        );
        const measuredHeight = Math.max(
            distance(topLeft, bottomLeft),
            distance(topRight, bottomRight)
        );

        const landscape = measuredWidth >= measuredHeight;
        const outputWidth = landscape ? 1400 : 884;
        const outputHeight = landscape ? 884 : 1400;

        const source = cv.imread(sourceCanvas);
        const destination = new cv.Mat();
        const srcPoints = cv.matFromArray(4, 1, cv.CV_32FC2, [
            topLeft.x, topLeft.y,
            topRight.x, topRight.y,
            bottomRight.x, bottomRight.y,
            bottomLeft.x, bottomLeft.y
        ]);
        const dstPoints = cv.matFromArray(4, 1, cv.CV_32FC2, [
            0, 0,
            outputWidth - 1, 0,
            outputWidth - 1, outputHeight - 1,
            0, outputHeight - 1
        ]);
        const transform = cv.getPerspectiveTransform(srcPoints, dstPoints);

        try {
            cv.warpPerspective(
                source,
                destination,
                transform,
                new cv.Size(outputWidth, outputHeight),
                cv.INTER_CUBIC,
                cv.BORDER_REPLICATE,
                new cv.Scalar()
            );
            const canvas = document.createElement('canvas');
            canvas.width = outputWidth;
            canvas.height = outputHeight;
            cv.imshow(canvas, destination);
            return canvas;
        } finally {
            source.delete();
            destination.delete();
            srcPoints.delete();
            dstPoints.delete();
            transform.delete();
        }
    };

    const copyCanvas = source => {
        const canvas = document.createElement('canvas');
        canvas.width = source.width;
        canvas.height = source.height;
        canvas.getContext('2d', { willReadFrequently: true }).drawImage(source, 0, 0);
        return canvas;
    };

    const extractDocument = async file => {
        const key = fingerprint(file);
        const cached = documentCache.get(key);
        if (cached) {
            return {
                canvas: copyCanvas(cached.canvas),
                meta: { ...cached.meta }
            };
        }

        const bitmap = await loadBitmap(file);
        const { canvas: sourceCanvas, sourceWidth, sourceHeight, scale: sourceScale } =
            canvasFromBitmap(bitmap, 2800);
        bitmap.close?.();

        const displayedAspect = Math.max(sourceCanvas.width, sourceCanvas.height) /
            Math.max(1, Math.min(sourceCanvas.width, sourceCanvas.height));

        if (displayedAspect >= 1.38 && displayedAspect <= 1.82) {
            const directCanvas = copyCanvas(sourceCanvas);
            const meta = {
                detected: true,
                fullFrame: true,
                areaRatio: 1,
                rectangularity: 1,
                aspectRatio: displayedAspect,
                sourceWidth,
                sourceHeight,
                cardWidth: sourceWidth,
                cardHeight: sourceHeight,
                detectionScore: 3
            };
            documentCache.set(key, { canvas: copyCanvas(directCanvas), meta });
            return { canvas: directCanvas, meta: { ...meta } };
        }

        const detectionMaxSide = 900;
        const detectionScale = Math.min(
            1,
            detectionMaxSide / Math.max(sourceCanvas.width, sourceCanvas.height)
        );
        const detectionCanvas = document.createElement('canvas');
        detectionCanvas.width = Math.max(1, Math.round(sourceCanvas.width * detectionScale));
        detectionCanvas.height = Math.max(1, Math.round(sourceCanvas.height * detectionScale));
        detectionCanvas.getContext('2d', { willReadFrequently: true }).drawImage(
            sourceCanvas,
            0,
            0,
            detectionCanvas.width,
            detectionCanvas.height
        );

        const cv = await ensureOpenCv();
        const candidate = findBestDocumentCandidate(cv, detectionCanvas);
        if (!candidate) {
            const error = new Error(
                'Không xác định được đầy đủ vùng CCCD/GPLX. Hãy chụp gần hơn, để đủ 4 cạnh giấy tờ và dùng nền tương phản.'
            );
            error.code = 'DOCUMENT_NOT_FOUND';
            throw error;
        }

        const pointScale = 1 / detectionScale;
        const points = orderPoints(candidate.vertices).map(point => ({
            x: Math.max(0, Math.min(sourceCanvas.width - 1, point.x * pointScale)),
            y: Math.max(0, Math.min(sourceCanvas.height - 1, point.y * pointScale))
        }));

        const warped = warpDocument(cv, sourceCanvas, points);
        const longPixelsInSource = Math.max(candidate.rectWidth, candidate.rectHeight) *
            pointScale / Math.max(sourceScale, 0.0001);
        const shortPixelsInSource = Math.min(candidate.rectWidth, candidate.rectHeight) *
            pointScale / Math.max(sourceScale, 0.0001);

        const meta = {
            detected: true,
            fullFrame: false,
            areaRatio: candidate.areaRatio,
            rectangularity: candidate.rectangularity,
            aspectRatio: candidate.aspectRatio,
            sourceWidth,
            sourceHeight,
            cardWidth: longPixelsInSource,
            cardHeight: shortPixelsInSource,
            detectionScore: candidate.score
        };

        documentCache.set(key, { canvas: copyCanvas(warped), meta });
        return { canvas: warped, meta: { ...meta } };
    };

    const analyzeCanvas = (canvas, meta) => {
        const maxSide = 760;
        const scale = Math.min(1, maxSide / Math.max(canvas.width, canvas.height));
        const width = Math.max(1, Math.round(canvas.width * scale));
        const height = Math.max(1, Math.round(canvas.height * scale));
        const sampleCanvas = document.createElement('canvas');
        sampleCanvas.width = width;
        sampleCanvas.height = height;
        const ctx = sampleCanvas.getContext('2d', { willReadFrequently: true });
        ctx.drawImage(canvas, 0, 0, width, height);

        const pixels = ctx.getImageData(0, 0, width, height).data;
        const gray = new Float32Array(width * height);
        let brightnessSum = 0;
        let darkCount = 0;
        let brightCount = 0;

        const gridSize = 6;
        const blockBright = new Uint32Array(gridSize * gridSize);
        const blockTotal = new Uint32Array(gridSize * gridSize);

        for (let y = 0; y < height; y += 1) {
            const blockY = Math.min(gridSize - 1, Math.floor((y / height) * gridSize));
            for (let x = 0; x < width; x += 1) {
                const index = (y * width + x) * 4;
                const luminance = 0.299 * pixels[index] +
                    0.587 * pixels[index + 1] +
                    0.114 * pixels[index + 2];
                gray[y * width + x] = luminance;
                brightnessSum += luminance;
                if (luminance < 45) darkCount += 1;
                if (luminance > 245) brightCount += 1;

                const blockX = Math.min(gridSize - 1, Math.floor((x / width) * gridSize));
                const blockIndex = blockY * gridSize + blockX;
                blockTotal[blockIndex] += 1;
                if (luminance > 245) blockBright[blockIndex] += 1;
            }
        }

        const totalPixels = Math.max(1, width * height);
        const brightness = brightnessSum / totalPixels;
        const darkRatio = darkCount / totalPixels;
        const brightRatio = brightCount / totalPixels;
        let maxBrightBlockRatio = 0;
        for (let index = 0; index < blockTotal.length; index += 1) {
            if (!blockTotal[index]) continue;
            maxBrightBlockRatio = Math.max(
                maxBrightBlockRatio,
                blockBright[index] / blockTotal[index]
            );
        }

        let laplacianSum = 0;
        let laplacianSquareSum = 0;
        let laplacianCount = 0;
        let edgeCount = 0;
        let edgeTotal = 0;

        for (let y = 1; y < height - 1; y += 2) {
            for (let x = 1; x < width - 1; x += 2) {
                const center = gray[y * width + x];
                const laplacian = gray[(y - 1) * width + x] +
                    gray[(y + 1) * width + x] +
                    gray[y * width + x - 1] +
                    gray[y * width + x + 1] -
                    4 * center;
                laplacianSum += laplacian;
                laplacianSquareSum += laplacian * laplacian;
                laplacianCount += 1;

                const gx = gray[y * width + x + 1] - gray[y * width + x - 1];
                const gy = gray[(y + 1) * width + x] - gray[(y - 1) * width + x];
                if (Math.hypot(gx, gy) > 35) edgeCount += 1;
                edgeTotal += 1;
            }
        }

        const laplacianMean = laplacianCount ? laplacianSum / laplacianCount : 0;
        const sharpness = laplacianCount
            ? Math.max(0, laplacianSquareSum / laplacianCount - laplacianMean * laplacianMean)
            : 0;
        const edgeDensity = edgeTotal ? edgeCount / edgeTotal : 0;

        const failures = [];
        const warnings = [];
        const cardShort = Math.min(meta.cardWidth || 0, meta.cardHeight || 0);
        const cardLong = Math.max(meta.cardWidth || 0, meta.cardHeight || 0);

        if (cardShort < thresholds.minCardShortSide || cardLong < thresholds.minCardLongSide) {
            failures.push('CCCD/GPLX trong ảnh có quá ít pixel. Hãy chụp gần hơn để chữ đủ lớn.');
        }
        if (!meta.fullFrame && meta.areaRatio < thresholds.minDocumentAreaRatio) {
            failures.push('Giấy tờ chiếm quá ít diện tích khung hình. Hãy đưa giấy tờ gần camera hơn.');
        } else if (!meta.fullFrame && meta.areaRatio < thresholds.warnDocumentAreaRatio) {
            warnings.push('Giấy tờ hơi nhỏ trong ảnh; hệ thống đã tự cắt nhưng nên chụp gần hơn.');
        }
        if (sharpness < thresholds.minSharpness) {
            failures.push('Phần giấy tờ bị mờ hoặc nhòe. Hãy giữ camera ổn định và chụp lại.');
        } else if (sharpness < thresholds.warnSharpness) {
            warnings.push('Chữ trên giấy tờ hơi mềm; nên giữ máy chắc hơn.');
        }
        if (brightness < thresholds.minBrightness || darkRatio > thresholds.maxDarkRatio) {
            failures.push('Vùng giấy tờ quá tối. Hãy chụp ở nơi đủ sáng.');
        }
        if (brightness > thresholds.maxBrightness || brightRatio > thresholds.maxBrightRatio) {
            failures.push('Vùng giấy tờ bị cháy sáng. Hãy giảm ánh sáng trực tiếp.');
        }
        if (brightRatio > thresholds.glareBrightRatio &&
            maxBrightBlockRatio > thresholds.glareBlockRatio) {
            failures.push('Có vùng lóa lớn trên giấy tờ. Hãy đổi góc chụp.');
        }
        if (edgeDensity < thresholds.minEdgeDensity) {
            failures.push('Chữ/chi tiết trên giấy tờ chưa đủ rõ để OCR ổn định.');
        } else if (edgeDensity < thresholds.warnEdgeDensity) {
            warnings.push('Mức chi tiết trên giấy tờ hơi thấp; OCR có thể cần bạn kiểm tra lại.');
        }

        return {
            passed: failures.length === 0,
            failures,
            warnings,
            metrics: {
                width: meta.sourceWidth,
                height: meta.sourceHeight,
                cardWidth: Math.round(cardLong),
                cardHeight: Math.round(cardShort),
                areaRatio: meta.areaRatio,
                sharpness,
                brightness,
                darkRatio,
                brightRatio,
                edgeDensity,
                perspectiveCorrected: !meta.fullFrame
            }
        };
    };

    const analyzeImage = async file => {
        const key = fingerprint(file);
        if (qualityCache.has(key)) return qualityCache.get(key);

        try {
            const extracted = await extractDocument(file);
            const result = analyzeCanvas(extracted.canvas, extracted.meta);
            qualityCache.set(key, result);
            return result;
        } catch (error) {
            const result = {
                passed: false,
                failures: [error?.message || 'Không xác định được vùng giấy tờ.'],
                warnings: [],
                metrics: {
                    width: 0,
                    height: 0,
                    cardWidth: 0,
                    cardHeight: 0,
                    areaRatio: 0,
                    sharpness: 0,
                    brightness: 0,
                    edgeDensity: 0,
                    perspectiveCorrected: false
                }
            };
            qualityCache.set(key, result);
            return result;
        }
    };

    const prepareForOcr = async file => {
        const extracted = await extractDocument(file);
        return {
            canvas: copyCanvas(extracted.canvas),
            meta: { ...extracted.meta }
        };
    };

    window.SmartCarDocumentVision = {
        analyzeImage,
        extractDocument,
        prepareForOcr,
        thresholds: { ...thresholds }
    };

    const createQualityHost = (panel, fileState, title) => {
        let host = panel.querySelector('[data-image-quality-host]');
        if (host) return host;
        host = document.createElement('div');
        host.dataset.imageQualityHost = '';
        host.className = 'border rounded-3 p-3 mt-3 bg-light';
        host.innerHTML = `
            <div class="d-flex justify-content-between align-items-center gap-2 flex-wrap mb-2">
                <strong>${title}</strong>
                <span class="badge bg-secondary" data-image-quality-badge>Chưa kiểm tra</span>
            </div>
            <div class="small text-muted" data-image-quality-body>
                Chọn đủ hai mặt. SmartCar sẽ tìm vùng thẻ, cắt nền và kiểm tra chất lượng trước khi OCR.
            </div>`;
        fileState?.insertAdjacentElement('afterend', host);
        return host;
    };

    const renderFace = (label, result) => {
        const icon = result.passed ? '✓' : '✕';
        const tone = result.passed ? 'text-success' : 'text-danger';
        const detail = result.failures.length
            ? result.failures.join(' ')
            : result.warnings.length
                ? result.warnings.join(' ')
                : 'Vùng giấy tờ đạt yêu cầu.';
        const areaText = result.metrics.areaRatio > 0 && result.metrics.areaRatio < 0.999
            ? ` · thẻ chiếm ${Math.round(result.metrics.areaRatio * 100)}% ảnh`
            : '';
        const cropText = result.metrics.perspectiveCorrected ? ' · đã tự cắt/chỉnh góc' : '';
        return `
            <div class="col-md-6">
                <div class="border rounded-3 p-2 h-100 bg-white">
                    <div class="fw-semibold ${tone}">${icon} ${label}</div>
                    <div class="small mt-1">${detail}</div>
                    <div class="small text-muted mt-1">
                        vùng thẻ ${result.metrics.cardWidth}×${result.metrics.cardHeight}px ·
                        độ nét ${Math.round(result.metrics.sharpness)} ·
                        sáng ${Math.round(result.metrics.brightness)}${areaText}${cropText}
                    </div>
                </div>
            </div>`;
    };

    const renderQuality = (host, frontResult, backResult, checking = false) => {
        const badge = host.querySelector('[data-image-quality-badge]');
        const body = host.querySelector('[data-image-quality-body]');
        if (checking) {
            badge.textContent = 'Đang tìm giấy tờ';
            badge.className = 'badge bg-secondary';
            body.innerHTML = 'Đang tìm 4 cạnh giấy tờ, cắt nền và phân tích ngay trên máy tính...';
            return;
        }

        const passed = frontResult?.passed && backResult?.passed;
        badge.textContent = passed ? 'Ảnh đạt yêu cầu' : 'Cần chụp lại';
        badge.className = `badge ${passed ? 'bg-success' : 'bg-danger'}`;
        body.innerHTML = `
            <div class="row g-2">
                ${renderFace('Mặt trước', frontResult)}
                ${renderFace('Mặt sau', backResult)}
            </div>
            <div class="small ${passed ? 'text-success' : 'text-danger'} fw-semibold mt-2">
                ${passed
                    ? '✓ Vùng giấy tờ đạt chất lượng. OCR sẽ dùng ảnh đã cắt/chỉnh thay vì toàn bộ nền.'
                    : '✕ Hãy chọn/chụp lại ảnh chưa đạt trước khi tiếp tục.'}
            </div>`;
    };

    const setMessage = (panel, message, type = 'warning') => {
        const selector = panel.matches('[data-ekyc-panel="citizen"]')
            ? '[data-ekyc-message]'
            : '[data-license-message]';
        const box = panel.querySelector(selector);
        if (!box) return;
        box.className = `alert alert-${type} ekyc-message mb-3`;
        box.textContent = message;
        box.classList.remove('d-none');
    };

    const panelConfig = panel => {
        const citizen = panel.matches('[data-ekyc-panel="citizen"]');
        return citizen
            ? {
                frontInput: document.getElementById('citizen-front-file'),
                backInput: document.getElementById('citizen-back-file'),
                fileState: panel.querySelector('[data-ekyc-file-state]'),
                ocrButton: panel.querySelector('[data-ekyc-ocr]'),
                manualButton: panel.querySelector('[data-ekyc-manual]'),
                submitButton: panel.closest('form')?.querySelector('button[type="submit"]'),
                title: 'Kiểm tra vùng và chất lượng ảnh CCCD'
            }
            : {
                frontInput: document.getElementById('license-front-file'),
                backInput: document.getElementById('license-back-file'),
                fileState: panel.querySelector('[data-license-file-state]'),
                ocrButton: panel.querySelector('[data-license-ocr]'),
                manualButton: panel.querySelector('[data-license-manual]'),
                submitButton: panel.closest('form')?.querySelector('button[type="submit"]'),
                title: 'Kiểm tra vùng và chất lượng ảnh GPLX'
            };
    };

    const runPanelCheck = async (panel, { render = true } = {}) => {
        const config = panelConfig(panel);
        const front = config.frontInput?.files?.[0];
        const back = config.backInput?.files?.[0];
        const host = createQualityHost(panel, config.fileState, config.title);

        if (!front || !back) {
            panel.dataset.imageQuality = 'pending';
            if (render) {
                const badge = host.querySelector('[data-image-quality-badge]');
                const body = host.querySelector('[data-image-quality-body]');
                badge.textContent = 'Chưa kiểm tra';
                badge.className = 'badge bg-secondary';
                body.textContent = 'Chọn đủ hai mặt giấy tờ để kiểm tra trước khi OCR.';
            }
            return { passed: false, incomplete: true };
        }

        panel.dataset.imageQuality = 'checking';
        config.ocrButton.disabled = true;
        if (render) renderQuality(host, null, null, true);

        try {
            const [frontResult, backResult] = await Promise.all([
                analyzeImage(front),
                analyzeImage(back)
            ]);
            const passed = frontResult.passed && backResult.passed;
            panel.dataset.imageQuality = passed ? 'pass' : 'fail';
            if (render) renderQuality(host, frontResult, backResult, false);
            config.ocrButton.disabled = !passed || panel.dataset.duplicateImages === 'true';
            if (!passed && panel.dataset.manualMode === 'true' && config.submitButton) {
                config.submitButton.disabled = true;
            }
            return { passed, frontResult, backResult };
        } catch (error) {
            panel.dataset.imageQuality = 'fail';
            config.ocrButton.disabled = true;
            if (render) {
                const badge = host.querySelector('[data-image-quality-badge]');
                const body = host.querySelector('[data-image-quality-body]');
                badge.textContent = 'Không kiểm tra được';
                badge.className = 'badge bg-danger';
                body.textContent = error?.message || 'Không thể phân tích ảnh. Hãy chọn ảnh khác.';
            }
            return { passed: false, error };
        }
    };

    const installPanel = panel => {
        if (!panel || panel.dataset.imageQualityInstalled === 'true') return;
        panel.dataset.imageQualityInstalled = 'true';
        const config = panelConfig(panel);
        if (!config.frontInput || !config.backInput || !config.ocrButton) return;

        createQualityHost(panel, config.fileState, config.title);
        const onChange = () => {
            panel.dataset.imageQuality = 'pending';
            void runPanelCheck(panel);
        };
        config.frontInput.addEventListener('change', onChange);
        config.backInput.addEventListener('change', onChange);
        if (config.frontInput.files?.[0] && config.backInput.files?.[0]) {
            void runPanelCheck(panel);
        }
    };

    const installOcrQualityGate = panel => {
        const state = panel.querySelector('[data-ekyc-ocr-state]');
        const nextButton = panel.querySelector('[data-ekyc-next-face]');
        const documentNumber = panel.querySelector('[name="CitizenIdVerification.DocumentNumber"]');
        if (!state || !nextButton || !documentNumber || state.dataset.ocrQualityObserved === 'true') return;
        state.dataset.ocrQualityObserved = 'true';

        const evaluate = () => {
            const match = state.textContent?.match(/(?:đọc được|đọc hợp lệ)\s+(\d+)\/7/i);
            if (!match) return;
            const fieldCount = Number(match[1]);
            const hasDocumentNumber = /^\d{12}$/.test(documentNumber.value || '');
            const passed = fieldCount >= 4 && hasDocumentNumber;
            panel.dataset.ocrQuality = passed ? 'pass' : 'fail';
            nextButton.disabled = !passed;
            if (!passed) {
                setMessage(
                    panel,
                    'OCR đọc được quá ít thông tin hợp lệ hoặc chưa đọc được số CCCD 12 chữ số. Hãy quay lại và chọn ảnh rõ hơn.',
                    'danger'
                );
            }
        };

        new MutationObserver(evaluate).observe(state, {
            childList: true,
            characterData: true,
            subtree: true
        });
    };

    document.addEventListener('click', async event => {
        const button = event.target.closest(
            '[data-ekyc-ocr], [data-license-ocr], [data-ekyc-manual], [data-license-manual]'
        );
        if (!button) return;
        const panel = button.closest('[data-ekyc-panel="citizen"], [data-ekyc-panel="license"]');
        if (!panel) return;

        if (panel.dataset.imageQualityBypass === 'true') {
            panel.dataset.imageQualityBypass = 'false';
            return;
        }

        const config = panelConfig(panel);
        if (!config.frontInput?.files?.[0] || !config.backInput?.files?.[0]) return;

        event.preventDefault();
        event.stopImmediatePropagation();
        const originalDisabled = button.disabled;
        button.disabled = true;

        const result = await runPanelCheck(panel);
        if (!result.passed) {
            setMessage(
                panel,
                'Ảnh giấy tờ chưa đạt yêu cầu. SmartCar chỉ OCR sau khi tìm được vùng thẻ rõ, đủ 4 cạnh và đủ nét.',
                'danger'
            );
            return;
        }

        panel.dataset.imageQualityBypass = 'true';
        button.disabled = originalDisabled && button !== config.ocrButton;
        if (!button.disabled) button.click();
    }, true);

    document.addEventListener('submit', event => {
        const form = event.target;
        if (!(form instanceof HTMLFormElement)) return;
        const panel = form.querySelector('[data-ekyc-panel="citizen"], [data-ekyc-panel="license"]');
        if (!panel || panel.dataset.imageQuality !== 'fail') return;
        event.preventDefault();
        event.stopImmediatePropagation();
        setMessage(panel, 'Không thể gửi hồ sơ vì ảnh giấy tờ chưa đạt yêu cầu chất lượng.', 'danger');
    }, true);

    const install = () => {
        document.querySelectorAll('[data-ekyc-panel="citizen"], [data-ekyc-panel="license"]')
            .forEach(panel => {
                installPanel(panel);
                if (panel.matches('[data-ekyc-panel="citizen"]')) installOcrQualityGate(panel);
            });
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => setTimeout(install, 0), { once: true });
    } else {
        setTimeout(install, 0);
    }
})();
