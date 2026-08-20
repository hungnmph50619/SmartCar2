(() => {
    const pathname = window.location.pathname.toLowerCase();
    if (!pathname.startsWith('/adminextensions')) {
        return;
    }

    const normalize = value => (value ?? '').replace(/\s+/g, ' ').trim();

    // Nghiệp vụ "cố tình không trả xe sau khi bị từ chối" là xử lý vi phạm
    // sau thời hạn trả, không phải một bước của màn hình duyệt gia hạn.
    document.querySelectorAll('details').forEach(details => {
        const summary = details.querySelector(':scope > summary');
        if (summary && normalize(summary.textContent).includes('Ghi nhận A cố tình không trả xe')) {
            details.remove();
        }
    });

    // Viết lại nguyên tắc theo vai trò nghiệp vụ, không dùng ký hiệu A/B.
    const principle = Array.from(document.querySelectorAll('.alert'))
        .find(element => normalize(element.textContent).startsWith('Nguyên tắc:'));
    if (principle) {
        principle.innerHTML = '<strong>Nguyên tắc xử lý:</strong> Nếu thời gian gia hạn trùng với đơn thuê kế tiếp, yêu cầu gia hạn thông thường sẽ không được duyệt. Với trường hợp bất khả kháng có ảnh và vị trí xác minh, SmartCar ưu tiên bố trí xe thay thế cho khách có đơn kế tiếp. Nếu không thể bố trí xe phù hợp, thực hiện hủy đơn kế tiếp, hoàn tiền và chỉ bồi thường thiệt hại thực tế có căn cứ.';
    }

    const replacements = [
        ['Xem đơn B →', 'Xem đơn kế tiếp →'],
        ['Ưu tiên đổi xe cho khách B', 'Đề xuất xe thay thế cho khách có đơn kế tiếp'],
        ['Khách B đã đồng ý xe và chênh lệch giá', 'Khách có đơn kế tiếp đã đồng ý xe thay thế và chênh lệch giá'],
        ['Không có xe khác phù hợp lịch của B.', 'Không có xe thay thế phù hợp lịch của đơn kế tiếp.'],
        ['Nếu B không đồng ý đổi xe', 'Nếu khách có đơn kế tiếp không chấp nhận xe thay thế'],
        ['Hủy đơn B & tạo quyết toán', 'Hủy đơn kế tiếp và xử lý hoàn tiền'],
        ['Đã liên hệ B và B không chấp nhận xe thay thế', 'Đã liên hệ khách có đơn kế tiếp và khách không chấp nhận xe thay thế'],
        ['Bồi thường thiệt hại thực tế', 'Bồi thường thiệt hại thực tế cho khách có đơn kế tiếp'],
        ['Căn cứ thiệt hại', 'Căn cứ bồi thường'],
        ['Trùng đơn #', 'Ảnh hưởng đơn kế tiếp #']
    ];

    const excludedTags = new Set(['SCRIPT', 'STYLE', 'OPTION']);
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    let node = walker.nextNode();
    while (node) {
        const parent = node.parentElement;
        if (!parent || !excludedTags.has(parent.tagName)) {
            let value = node.nodeValue ?? '';
            for (const [from, to] of replacements) {
                if (value.includes(from)) {
                    value = value.split(from).join(to);
                }
            }
            node.nodeValue = value;
        }
        node = walker.nextNode();
    }

    // Các đoạn mô tả dài cần viết lại nguyên câu để tránh còn sót A/B.
    Array.from(document.querySelectorAll('.small.text-muted')).forEach(element => {
        const text = normalize(element.textContent);
        if (text.includes('Hủy đơn B, hoàn các khoản B đã thanh toán')) {
            element.textContent = 'Nếu khách có đơn kế tiếp không chấp nhận xe thay thế, hủy đơn kế tiếp và hoàn toàn bộ khoản khách đã thanh toán. Chỉ nhập khoản bồi thường khi có thiệt hại thực tế và căn cứ rõ ràng; khoản bồi thường được xử lý theo chính sách từ tiền cọc của khách đang thuê.';
        }
    });
})();
