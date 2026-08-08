(() => {
    const shortMessage = text => {
        const value = (text || '').trim();
        if (!value) return value;

        if (/OpenCV|khởi tạo được .*vùng giấy tờ|tải được .*OpenCV/i.test(value)) {
            return 'Không thể kiểm tra ảnh lúc này. Hãy tải lại trang và thử lại.';
        }
        if (/không xác định được.*vùng|không thấy.*4 cạnh|đầy đủ vùng/i.test(value)) {
            return 'Không thấy đủ 4 góc giấy tờ. Hãy chụp lại gần hơn.';
        }
        if (/quá ít pixel|chiếm quá ít|quá nhỏ/i.test(value)) {
            return 'Ảnh chụp quá xa. Hãy đưa giấy tờ gần hơn.';
        }
        if (/mờ|nhòe|hơi mềm|độ nét/i.test(value)) {
            return 'Ảnh bị mờ. Hãy chụp lại và giữ máy ổn định.';
        }
        if (/quá tối/i.test(value)) {
            return 'Ảnh quá tối. Hãy chụp ở nơi đủ sáng.';
        }
        if (/cháy sáng|lóa/i.test(value)) {
            return 'Ảnh bị lóa. Hãy đổi góc chụp.';
        }
        if (/chi tiết.*chưa đủ rõ|mức chi tiết/i.test(value)) {
            return 'Chữ chưa đủ rõ. Hãy chụp lại gần hơn.';
        }
        if (/OCR đọc được quá ít|chưa đọc được số CCCD/i.test(value)) {
            return 'Ảnh chưa đủ rõ để đọc thông tin. Vui lòng chụp lại.';
        }
        if (/Ảnh giấy tờ chưa đạt yêu cầu/i.test(value)) {
            return 'Ảnh chưa đạt. Vui lòng chụp lại ảnh rõ, đủ 4 góc và đủ sáng.';
        }
        if (/Không thể gửi hồ sơ vì ảnh giấy tờ chưa đạt/i.test(value)) {
            return 'Ảnh chưa đạt. Vui lòng chụp lại trước khi gửi hồ sơ.';
        }

        return value;
    };

    const simplifyHost = host => {
        const heading = host.querySelector('strong');
        if (heading) {
            if (/CCCD/i.test(heading.textContent || '')) heading.textContent = 'Kiểm tra ảnh CCCD';
            if (/GPLX/i.test(heading.textContent || '')) heading.textContent = 'Kiểm tra ảnh GPLX';
        }

        const badge = host.querySelector('[data-image-quality-badge]');
        const body = host.querySelector('[data-image-quality-body]');
        if (!body) return;

        const rawText = body.textContent || '';
        const systemError = /OpenCV|Không thể kiểm tra ảnh lúc này/i.test(rawText);

        body.querySelectorAll('.col-md-6').forEach(card => {
            const detail = [...card.querySelectorAll('.small')]
                .find(node => !node.classList.contains('text-muted'));
            if (detail) detail.textContent = shortMessage(detail.textContent || '');

            card.querySelectorAll('.small.text-muted').forEach(metrics => {
                if (/vùng thẻ|độ nét|sáng|0×0px/i.test(metrics.textContent || '')) {
                    metrics.classList.add('d-none');
                }
            });
        });

        const summary = body.querySelector('.fw-semibold.mt-2');
        if (summary) {
            if (systemError) {
                summary.textContent = 'Không thể kiểm tra ảnh. Hãy tải lại trang và thử lại.';
                summary.className = 'small text-danger fw-semibold mt-2';
            } else if (/✓|đạt/i.test(summary.textContent || '')) {
                summary.textContent = '✓ Ảnh đạt. Bạn có thể tiếp tục.';
                summary.className = 'small text-success fw-semibold mt-2';
            } else {
                summary.textContent = '✕ Vui lòng chụp lại ảnh chưa đạt.';
                summary.className = 'small text-danger fw-semibold mt-2';
            }
        } else if (!body.querySelector('.row')) {
            if (/Đang tìm|Đang.*phân tích|Đang.*cắt/i.test(rawText)) {
                body.textContent = 'Đang kiểm tra ảnh...';
            } else if (/Chọn đủ hai mặt/i.test(rawText)) {
                body.textContent = 'Chọn đủ hai mặt để SmartCar kiểm tra ảnh.';
            } else {
                const simplified = shortMessage(rawText);
                if (simplified !== rawText) body.textContent = simplified;
            }
        }

        if (badge) {
            const badgeText = (badge.textContent || '').trim();
            if (systemError) {
                badge.textContent = 'Thử lại';
                badge.className = 'badge bg-warning text-dark';
            } else if (/Đang/i.test(badgeText)) {
                badge.textContent = 'Đang kiểm tra';
            } else if (/Ảnh đạt yêu cầu|^Đạt$/i.test(badgeText)) {
                badge.textContent = 'Đạt';
            } else if (/Cần chụp lại|Chụp lại/i.test(badgeText)) {
                badge.textContent = 'Chụp lại';
            }
        }
    };

    const simplifyAlerts = () => {
        document.querySelectorAll('[data-ekyc-message], [data-license-message]').forEach(box => {
            const current = box.textContent || '';
            const simplified = shortMessage(current);
            if (simplified !== current.trim()) box.textContent = simplified;
        });
    };

    let scheduled = false;
    const apply = () => {
        if (scheduled) return;
        scheduled = true;
        requestAnimationFrame(() => {
            scheduled = false;
            document.querySelectorAll('[data-image-quality-host]').forEach(simplifyHost);
            simplifyAlerts();
        });
    };

    const start = () => {
        apply();
        new MutationObserver(apply).observe(document.body, {
            childList: true,
            subtree: true,
            characterData: true
        });
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start, { once: true });
    } else {
        start();
    }
})();
