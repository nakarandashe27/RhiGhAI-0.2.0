# Что стоит заимствовать

## Contract-first

Из community RhinoMCP: явные DTO/JSON Schema, тесты контрактов, реестр операций, ownership metadata и GH diagnostics. Заимствуем идеи и совместимые MIT-фрагменты только с атрибуцией.

## Официальный app-server, собственный адаптер

Из Codex app-server: ChatGPT OAuth, account/read/logout, model/list, rate limits, thread start/resume, turn/start с output schema, streaming events и interrupt. В RhiGhAI нужен собственный небольшой C# JSONL-клиент и закреплённая версия, чтобы Experimental API не растёкся по коду.

## Нативный panel и процесс

Из официального McNeel RhinoMCP: удачные паттерны dockable panel, transcript/status, snapshot selection/context и управления дочерним процессом. Не переносим auto-grant произвольных tools.

## Параметры вместо одноразовой геометрии

Из Reer/SmartHopper и Grasshopper: именованные типизированные входы, пересчёт preview, сохранение алгоритма и отдельный bake.

## Инкрементальный диалог

Codex thread продолжает задачу, а «Начать заново» создаёт новый thread и новый GH-компонент. Для каждого документа соответствие хранится локально, не в файле 3dm.

## Инспектируемость

Показывать пользователю план, детерминированный C#-эквивалент, стадии, ошибки, попытки исправления и итог. Это важнее «магии».

## Узкая библиотека операций

Сначала покрыть только кривые, поверхности, extrusion/Brep, простые Boolean, transforms/copy, layers и attributes. Каждая операция должна иметь валидатор, исполнитель, визуализатор C# и тесты.

Вместе эти элементы создают [[delta]], не раздувая MVP.
