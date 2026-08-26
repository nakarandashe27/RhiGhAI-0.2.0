# Исследование

Проверено 24 августа 2026 года. Здесь отделены выпущенные возможности от заявленных планов.

## Короткий вывод

AI для CAD уже не гипотеза: узкие продукты и команды получили заметное финансирование — [MecAgent](https://mecagent.com/blog/revolutionizing-cad-with-ai), [Adam](https://adam.new/blog/seed), [Camfer](https://camfer.dev/blog/the-story-so-far/), [Leo](https://www.getleo.ai/blog/leo-ai-raises-9-7m-to-build-the-world-s-first-ai-for-mechanical-engineering) и [Backflip](https://www.businesswire.com/news/home/20241218887310/en/Backflip-Releases-AI-Model-That-Turns-Text-Into-Physical-Reality-Backed-By-%2430M-from-NEA-and-Andreessen-Horowitz). Однако это не доказывает спрос именно на RhiGhAI: рынок широк, а замена зрелого CAD слишком дорога — показателен итог [Ondsel](https://www.ondsel.com/blog/goodbye/).

В Rhino/Grasshopper уже есть [Ant](https://rhino-ant.ai/), [Reer](https://www.reer.co/), [SmartHopper](https://github.com/architects-toolkit/SmartHopper), общественный [RhinoMCP](https://github.com/jingcheng-chen/rhinomcp) и официальный, пока WIP, [McNeel RhinoMCP](https://github.com/mcneel/RhinoMCP). Значит, «чат внутри Rhino» сам по себе не является преимуществом.

Повторяющиеся жалобы пользователей: отдельная API-оплата и непонятные лимиты, ошибки схем и моделей, большой расход токенов, медленная генерация, непрозрачность и хрупкая установка. Примеры: [Kea](https://discourse.mcneel.com/t/ai-assistant-for-writing-modifying-scripts-in-grasshopper-kea-plug-in/207226/15), [SmartHopper](https://discourse.mcneel.com/t/smarthopper-a-deeply-integrated-ai-assistant-for-grasshopper/207407), [обсуждение native GH C#](https://www.reddit.com/r/rhino/comments/1ppiau7/natural_language_to_native_grasshopper_c_script/) и [лимиты Claude](https://discourse.mcneel.com/t/claude-is-quickly-running-out-of-credits/221827).

## Вывод для продукта

RhiGhAI следует позиционировать не как ещё один text-to-CAD, а как **контролируемый слой исполнения намерений** для Rhino 8 и Grasshopper 1:

- вход через ChatGPT без API-ключа;
- типизированный план и allowlist вместо произвольного кода;
- один ответ — одна отменяемая транзакция;
- откат и ограниченный цикл исправления;
- проверка актуальности документа и выделения;
- видимый эквивалент C# и диагностический след;
- редактируемый параметрический результат.

Технический вывод: строить небольшой собственный core поверх закреплённой версии официального Codex app-server. Из RhinoMCP заимствовать только отдельные MIT-паттерны интерфейса, процесса и контекста с атрибуцией, но не форкать его целиком.

См. [[analogs]], [[methodologies]], [[what-to-steal]], [[what-to-avoid]] и [[delta]].
