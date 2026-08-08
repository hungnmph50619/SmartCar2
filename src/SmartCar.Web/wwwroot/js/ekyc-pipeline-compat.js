(() => {
    const repair = root => {
        const scope = root instanceof Element ? root : document;
        scope.querySelectorAll?.('[data-document-gate-host]').forEach(host => {
            if (host.querySelector('[data-document-gate-body]')) return;
            const body = host.querySelector('[data-pipeline-document-body]');
            if (body) body.dataset.documentGateBody = '';
        });
    };

    const install = () => {
        repair(document);
        new MutationObserver(records => {
            records.forEach(record => {
                if (record.target instanceof Element) repair(record.target.closest('[data-document-gate-host]') || record.target);
                record.addedNodes.forEach(node => { if (node instanceof Element) repair(node); });
            });
        }).observe(document.body, { childList: true, subtree: true });
    };

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', install, { once: true });
    else install();
})();