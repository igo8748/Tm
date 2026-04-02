(function () {
    'use strict';

    var lampac_host = '{localhost}';
    var network = new Lampa.Reguest();

    function getToken() {
        return Lampa.Storage.get('token', '')
            || Lampa.Storage.get('lampac_unic_id', '')
            || Lampa.Storage.get('account_email', '');
    }

    function apiUrl(path) {
        var token = getToken();
        var sep = path.indexOf('?') >= 0 ? '&' : '?';
        return lampac_host + path + (token ? sep + 'token=' + encodeURIComponent(token) : '');
    }

    // =============================================
    //  Перехват кнопки "Подписаться"
    // =============================================
    function initSubscribeOverride() {
        Lampa.Listener.follow('full', function (e) {
            if (e.type === 'complite') {
                setTimeout(function () {
                    overrideSubscribeButton(e.object);
                }, 300);
            }
        });
    }

    function overrideSubscribeButton(obj) {
        if (!obj || !obj.card || !obj.card.number_of_seasons) return;

        var card = obj.card;
        var button;

        try {
            button = obj.activity.render().find('.button--subscribe');
        } catch(e) { return; }

        if (!button || !button.length) return;
        button.removeClass('hide');
        button.off('hover:enter');

        // Проверяем статус
        checkStatus(card.id, function (status) {
            if (status.subscribed) {
                button.addClass('active').find('path').attr('fill', 'currentColor');
            }

            button.on('hover:enter', function () {
                if (!status.linked) {
                    showLinkDialog();
                } else {
                    showVoiceMenu(card, button, status);
                }
            });
        });
    }

    // =============================================
    //  Меню выбора озвучки
    // =============================================
    function showVoiceMenu(card, button, currentStatus) {
        var title = card.name || card.title || '';
        var year = card.first_air_date ? parseInt(card.first_air_date) : (card.year || 0);
        var tmdbId = card.id || 0;

        Lampa.Loading.start(function () {
            network.clear();
            Lampa.Loading.stop();
        });

        // Запрашиваем озвучки — сервер сам определит актуальный сезон
        network.clear();
        network.timeout(15000);
        network["native"](apiUrl('/api/tg/voices?title=' + encodeURIComponent(title) + '&year=' + year + '&season=0&tmdb_id=' + tmdbId), function (result) {
            Lampa.Loading.stop();

            var season = result.season || card.number_of_seasons || 1;
            var items = [];

            // Текущие подписки — кнопки отписки
            if (currentStatus && currentStatus.subscribed && currentStatus.voices) {
                currentStatus.voices.forEach(function(v) {
                    items.push({
                        title: '❌ Отписаться: ' + (v || 'Любая озвучка'),
                        unsubscribe: true,
                        voice: v
                    });
                });

                if (items.length > 0) {
                    items.push({ title: '', separator: true });
                }
            }

            // Подписка без озвучки (оригинал)
            items.push({
                title: '🔔 Любая озвучка (оригинал)',
                subtitle: 'Уведомление при выходе серии',
                voice: '',
                orid: '',
                voice_id: 0,
                collaps_orid: '',
                voice_source: ''
            });

            // Озвучки из Mirage + Collaps
            if (result.success && result.voices && result.voices.length > 0) {
                items.push({ title: 'Озвучки:', separator: true });

                result.voices.forEach(function (v) {
                    var src = v.source || 'mirage';
                    var badge = src === 'collaps' ? ' [C]' : '';
                    items.push({
                        title: '🎙 ' + v.name + badge,
                        voice: v.name,
                        orid: result.orid || '',
                        voice_id: v.id || 0,
                        collaps_orid: result.collaps_orid || '',
                        voice_source: src
                    });
                });
            }

            Lampa.Select.show({
                title: 'TG Уведомления',
                items: items,
                onSelect: function (a) {
                    Lampa.Controller.toggle('content');

                    if (a.unsubscribe) {
                        doUnsubscribe(card, button, a.voice);
                    } else if (typeof a.voice !== 'undefined') {
                        doSubscribe(card, a.voice, season, button, a.orid, a.voice_id, a.collaps_orid, a.voice_source);
                    }
                },
                onBack: function () {
                    Lampa.Controller.toggle('content');
                }
            });
        }, function () {
            Lampa.Loading.stop();

            // Fallback — если Mirage недоступен, просто подписка без озвучки
            var items = [
                { title: '🔔 Подписаться (без озвучки)', voice: '', orid: '', voice_id: 0 }
            ];

            if (currentStatus && currentStatus.subscribed) {
                items.unshift({ title: '❌ Отписаться', unsubscribe: true, voice: '' });
            }

            Lampa.Select.show({
                title: 'TG Уведомления',
                items: items,
                onSelect: function (a) {
                    Lampa.Controller.toggle('content');
                    if (a.unsubscribe) doUnsubscribe(card, button, null);
                    else doSubscribe(card, '', card.number_of_seasons || 1, button, '', 0);
                },
                onBack: function () { Lampa.Controller.toggle('content'); }
            });
        });
    }

    // =============================================
    //  API
    // =============================================
    function doSubscribe(card, voice, season, button, orid, voiceId, collapsOrid, voiceSource) {
        var data = JSON.stringify({
            tmdb_id: card.id,
            title: card.name || card.title || '',
            voice: voice,
            season: season,
            episode: 0,
            mirage_orid: orid || '',
            mirage_voice_id: voiceId || 0,
            voice_episode: 0,
            collaps_orid: collapsOrid || '',
            voice_source: voiceSource || ''
        });

        network.clear();
        network["native"](apiUrl('/api/tg/subscribe'), function (result) {
            if (result.success) {
                var msg = voice ? '🔔 Подписка: ' + voice : '🔔 Подписка оформлена!';
                Lampa.Noty.show(msg);
                button.addClass('active').find('path').attr('fill', 'currentColor');
            } else if (result.msg === 'not_linked') {
                showLinkDialog();
            } else {
                Lampa.Noty.show('Ошибка: ' + (result.msg || ''));
            }
        }, function () {
            Lampa.Noty.show('Ошибка подписки');
        }, data, { dataType: 'json', contentType: 'application/json' });
    }

    function doUnsubscribe(card, button, voice) {
        var data = JSON.stringify({ tmdb_id: card.id, voice: voice });

        network.clear();
        network["native"](apiUrl('/api/tg/unsubscribe'), function (result) {
            if (result.success) {
                Lampa.Noty.show('Подписка отменена');
                // Проверяем остались ли другие подписки
                checkStatus(card.id, function(st) {
                    if (!st.subscribed) {
                        button.removeClass('active').find('path').attr('fill', 'transparent');
                    }
                });
            }
        }, function () {
            Lampa.Noty.show('Ошибка');
        }, data, { dataType: 'json', contentType: 'application/json' });
    }

    function checkStatus(tmdb_id, callback) {
        network.clear();
        network.timeout(5000);
        network["native"](apiUrl('/api/tg/status?tmdb_id=' + tmdb_id), function (result) {
            callback(result || { success: false, linked: false, subscribed: false, voices: [] });
        }, function () {
            callback({ success: false, linked: false, subscribed: false, voices: [] });
        });
    }

    // =============================================
    //  Привязка Telegram
    // =============================================
    function showLinkDialog() {
        network.clear();
        network["native"](apiUrl('/api/tg/link'), function (result) {
            if (!result.success || !result.link) {
                Lampa.Noty.show('Бот не запущен');
                return;
            }

            Lampa.Select.show({
                title: 'Привязка Telegram',
                items: [
                    { title: 'Открыть ссылку', subtitle: result.link, link: result.link },
                    { title: 'Показать ссылку', show_link: result.link }
                ],
                onSelect: function (a) {
                    Lampa.Controller.toggle('content');
                    if (a.link) {
                        if (typeof Android !== 'undefined' && Android.openBrowser) Android.openBrowser(a.link);
                        else if (window.open) window.open(a.link, '_blank');
                    }
                    if (a.show_link) Lampa.Noty.show(a.show_link);
                },
                onBack: function () { Lampa.Controller.toggle('content'); }
            });
        }, function () {
            Lampa.Noty.show('Модуль TelegramBot недоступен');
        });
    }

    // =============================================
    //  Init
    // =============================================
    function init() {
        initSubscribeOverride();
        console.log('[TG-Notify] Plugin loaded v2 (with voices)');
    }

    if (window.appready) init();
    else Lampa.Listener.follow('app', function (e) { if (e.type === 'ready') init(); });

})();
