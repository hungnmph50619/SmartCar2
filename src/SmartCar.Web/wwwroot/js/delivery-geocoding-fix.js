(() => {
    const nativeFetch = window.fetch.bind(window);

    window.fetch = (input, init) => {
        const rawUrl = typeof input === 'string'
            ? input
            : input instanceof Request
                ? input.url
                : String(input);

        try {
            const url = new URL(rawUrl, window.location.origin);
            const isNominatimReverse =
                url.hostname === 'nominatim.openstreetmap.org' &&
                url.pathname === '/reverse';

            if (isNominatimReverse) {
                const lat = url.searchParams.get('lat');
                const lon = url.searchParams.get('lon');

                if (lat && lon) {
                    const localUrl =
                        `/api/geocoding/reverse?lat=${encodeURIComponent(lat)}` +
                        `&lon=${encodeURIComponent(lon)}`;

                    return nativeFetch(localUrl, {
                        method: 'GET',
                        headers: {
                            'Accept': 'application/json'
                        }
                    });
                }
            }
        } catch {
            // Nếu URL không phân tích được thì giữ nguyên request ban đầu.
        }

        return nativeFetch(input, init);
    };
})();
