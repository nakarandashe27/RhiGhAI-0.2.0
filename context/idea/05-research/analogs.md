# Аналоги

Состояние проверено 24 августа 2026 года.

| Игрок | Фокус | Что важно для RhiGhAI |
|---|---|---|
| [Ant](https://rhino-ant.ai/) | AI-помощник в Rhino/Grasshopper | Подтверждает спрос на встроенный диалог, но не уникальность UI |
| [Reer](https://www.reer.co/) | Генерация и редактирование GH-логики | Подтверждает ценность редактируемого параметрического результата |
| [SmartHopper](https://github.com/architects-toolkit/SmartHopper) | Open-source AI-инструменты Grasshopper | Показывает цену API-ключей, совместимости моделей и поддержки схем |
| [McNeel RhinoMCP](https://github.com/mcneel/RhinoMCP) | Официальный WIP-мост к Codex/Claude/Gemini | Самая близкая угроза; roadmap нельзя считать выпущенной функцией |
| [Community RhinoMCP](https://github.com/jingcheng-chen/rhinomcp) | MCP-сервер и контракты Rhino/GH | Полезны схемы, тесты контрактов, ownership metadata и диагностика GH |
| [CADABRA](https://www.cadabra.ai/) | AI-автоматизация CAD | Поддерживает модель «структурный план, затем действие» |
| [Adam](https://adam.new/) | AI mechanical design | Демонстрирует узкий инженерный workflow вместо универсального чата |
| [Zoo](https://zoo.dev/) | Программируемый CAD и Text-to-CAD | Сильный plan–act–observe цикл, но другой стек и собственная среда |
| [ArgilCAD](https://argilcad.com/) | AI CAD copilot | Подтверждает спрос на повторяемые автоматизации |
| [QuantCAD](https://www.quantcad.com/) | Conversational CAD | Конкурирует обещанием natural-language моделирования |
| [Hestus](https://www.hestus.co/) | AI для проектирования/производства | Напоминает не заявлять инженерную корректность без расчётов |
| [DraftAid](https://draftaid.io/) | Автоматизация чертежей | Пример полезного узкого клина вместо замены всего CAD |
| [MecAgent](https://mecagent.com/) | AI-агенты для mechanical CAD | Подтверждает движение рынка к действиям, а не только генерации текста |

## Рыночный сигнал

Финансирование Adam, Camfer, Leo, MecAgent и Backflip показывает интерес инвесторов к AI-CAD, но не заменяет проверку локального пользовательского сценария. Для MVP важнее три приёмочных workflow из [[../04-mvp/success-metrics]], чем широкая рыночная история.

## Главная конкурентная развилка

Если официальный RhinoMCP реализует заявленный panel, Codex-интеграцию, выбор/контекст, Stop, Undo и цикл Grasshopper, преимущество «AI внутри Rhino» исчезнет. Защитимая часть RhiGhAI — Rhino 8/GH1, ChatGPT OAuth без ключа, строгий TaskPlan, транзакции, fingerprint и предсказуемые ограничения.
