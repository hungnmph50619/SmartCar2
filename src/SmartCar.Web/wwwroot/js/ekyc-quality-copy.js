(() => {
    const normalizeText = value => (value || '').replace(/\s+/g, ' ').trim();

    const friendlyMessage = text => {
        const value = normalizeText(text);
        if (!value) return value;

        if (/không tải được OpenCV|tải được .*OpenCV/i.test(value)) {
            return 'Bộ kiểm tra ảnh chưa tải được. Đây là lỗi hệ thống, không phải lỗi ảnh CCCD. Hãy kiểm tra Internet rồi tải lại trang.';
        }
        if (/không khởi tạo được OpenCV|khởi tạo được .*vùng giấy tờ/i.test(value)) {
            return 'Bộ kiểm tra ảnh chưa khởi động được. Đây là lỗi hệ thống, không phải lỗi ảnh CCCD. Hãy tải lại trang rồi thử lại.';
        }
        if (/không xác định được.*vùng|không thấy.*4 cạnh|đầy đủ vùng/i.test(value)) {
            return 'Không nhận ra đủ 4 góc giấy tờ. Hãy đặt thẻ phẳng, chụp gần hơn và để toàn bộ thẻ trong khung.';
        }
        if (/quá ít pixel|chiếm quá ít|quá nhỏ/i.test(value)) {
            return 'Giấy tờ ở quá xa nên chữ quá nhỏ. Hãy đưa thẻ gần camera hơn.';
        }
        if (/mờ|nhòe|hơi mềm|độ nét/i.test(value)) {
            return 'Ảnh bị mờ hoặc rung. Hãy giữ máy ổn định và chụp lại.';
        }
        if (/quá tối/i.test(value)) {
            return 'Ảnh quá tối. Hãy chụp ở nơi sáng hơn.';
        }
        if (/cháy sáng|lóa/i.test(value)) {
            return 'Ảnh bị lóa hoặc cháy sáng. Hãy đổi góc chụp và tránh ánh sáng chiếu trực tiếp vào thẻ.';
        }
        if (/chi tiết.*chưa đủ rõ|mức chi tiết/i.test(value)) {
            return 'Chữ trên giấy tờ chưa đủ rõ để đọc. Hãy chụp gần hơn và lấy nét vào thẻ.';
        }
        if (/OCR đọc được quá ít|chưa đọc được số CCCD/i.test(value)) {
            return 'Hệ thống chưa đọc được đủ thông tin trên CCCD. Hãy kiểm tra ảnh có rõ chữ, đủ 4 góc và không bị lóa.';
        }
        if (/Ảnh giấy tờ chưa đạt yêu cầu/i.test(value)) {
            return 'Ảnh chưa đạt yêu cầu. Hãy xem nguyên nhân ở từng mặt bên dưới và chụp lại mặt chưa đạt.';
        }
        if (/Không thể gửi hồ sơ vì ảnh giấy tờ chưa đạt/i.test(value)) {
            return 'Chưa thể gửi hồ sơ vì còn ảnh chưa đạt. Hãy chụp lại mặt được đánh dấu đỏ.';
        }

        return value;
    };

    const isSystemErrorText = text => /OpenCV|Bộ kiểm tra ảnh chưa tải được|Bộ kiểm tra ảnh chưa khởi động được|Không thể kiểm tra ảnh lúc này/i.test(text || '');

    const simplifyHost = host => {
        const heading = host.querySelector('strong');
        if (heading) {
            if (/CCCD/i.test(heading.textContent || '')) heading.textContent = 'Kiểm tra ảnh CCCD';
            if (/GPLX/i.test(heading.textContent || '')) heading.textContent = 'Kiểm tra ảnh GPLX';
        }

        const badge = host.querySelector('[data-image-quality-badge]');
        const body = host.querySelector('[data-image-quality-body]');
        if (!body) return;

        const rawText = normalizeText(body.textContent || '');
        const cardIssues = [];

        body.querySelectorAll('.col-md-6').forEach(card => {
            const titleNode = card.querySelector('.fw-semibold');
            const label = normalizeText(titleNode?.textContent || '')
                .replace(/^[✓✕×]\s*/, '') || 'Ảnh';
            const detail = [...card.querySelectorAll('.small')]
                .find(node => !node.classList.contains('text-muted'));

            if (detail) {
                const friendly = friendlyMessage(detail.textContent || '');
                detail.textContent = friendly;
                if (!/^Ảnh đạt|^Vùng giấy tờ đạt/i.test(friendly)) {
                    cardIssues.push({ label, message: friendly, system: isSystemErrorText(friendly) });
                }
            }

            card.querySelectorAll('.small.text-muted').forEach(metrics => {
                if (/vùng thẻ|độ nét|sáng|0×0px|px\s*·/i.test(metrics.textContent || '')) {
                    metrics.classList.add('d-none');
                }
            });
        });

        const hasSystemError = cardIssues.some(issue => issue.system) || isSystemErrorText(rawText);
        const summary = body.querySelector('.fw-semibold.mt-2');
        if (summary) {
            if (hasSystemError) {
                summary.textContent = 'Lỗi hệ thống: SmartCar chưa tải được bộ kiểm tra ảnh. Ảnh CCCD của bạn chưa được đánh giá. Hãy kiểm tra Internet rồi tải lại trang.';
                summary.className = 'small text-danger fw-semibold mt-2';
            } else if (!cardIssues.length && /✓|đạt/i.test(summary.textContent || '')) {
                summary.textContent = '✓ Ảnh đạt. Bạn có thể tiếp tục.';
                summary.className = 'small text-success fw-semibold mt-2';
            } else if (cardIssues.length) {
                const causes = cardIssues
                    .map(issue => `${issue.label}: ${issue.message}`)
                    .join(' ');
                summary.textContent = `Nguyên nhân: ${causes}`;
                summary.className = 'small text-danger fw-semibold mt-2';
            } else {
                summary.textContent = '✕ Có ảnh chưa đạt. Hãy xem nguyên nhân ở từng mặt và chụp lại.';
                summary.className = 'small text-danger fw-semibold mt-2';
            }
        } else if (!body.querySelector('.row')) {
            if (/Đang tìm|Đang.*phân tích|Đang.*cắt/i.test(rawText)) {
                body.textContent = 'Đang kiểm tra ảnh...';
            } else if (/Chọn đủ hai mặt/i.test(rawText)) {
                body.textContent = 'Chọn đủ hai mặt để SmartCar kiểm tra ảnh.';
            } else {
                const friendly = friendlyMessage(rawText);
                if (friendly !== rawText) body.textContent = friendly;
            }
        }

        if (badge) {
            const badgeText = normalizeText(badge.textContent || '');
            if (hasSystemError) {
                badge.textContent = 'Lỗi hệ thống';
                badge.className = 'badge bg-danger';
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
            const current = normalizeText(box.textContent || '');
            const friendly = friendlyMessage(current);
            if (friendly !== current) box.textContent = friendly;
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
