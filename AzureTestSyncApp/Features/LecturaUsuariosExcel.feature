Feature: Lectura de usuarios desde Excel con estructura institucional
  Como sistema de envío masivo
  Quiero leer un archivo Excel ignorando las primeras 4 filas de metadatos
  Para obtener una lista limpia de destinatarios válida para el envío

  Background: Estructura del Archivo (Simulación de Skip 4)
    # Aquí definimos exactamente las filas que tu código ignora con .Skip(4)
    Given que genero un archivo Excel temporal
    And agrego en la Fila 1 el valor "REPORTE DE USUARIOS"
    And agrego en la Fila 2 el valor "Fecha: 25/11/2025"
    And dejo la Fila 3 vacía
    And agrego en la Fila 4 los encabezados "CÉDULA", "NOMBRES", "EMAIL"

  Scenario Outline: Importación y validación de datos de usuarios
    # El formato de entrada simula las filas a partir de la 5: "Cedula,Nombre,Email" separados por ";"
    Given que agrego las siguientes filas de datos a partir de la Fila 5: "<datos_entrada>"
    When ejecuto el servicio LeerUsuariosDesdeExcel
    Then la cantidad de registros leídos debe ser <cantidad_esperada>
    And el primer usuario de la lista debe tener el email "<email_primero>"
    And el último usuario de la lista debe tener el email "<email_ultimo>"

    Examples: Casos de prueba
      | datos_entrada                                                                 | cantidad_esperada | email_primero   | email_ultimo    | Notas                                                                 |
      | 1002003001,Juan Pérez,juan@email.com;1755555555,María López,maria@test.com    | 2                 | juan@email.com  | maria@test.com  | CASO EXACTO: 2 filas válidas después del salto de 4.                  |
      | 1001,Anthony Dev,antho@test.com                                               | 1                 | antho@test.com  | antho@test.com  | UN SOLO USUARIO: Verifica que lea la Fila 5 aunque sea la única.      |
      | 1001,Incompleto,;1002,Completo,rodman@test.com                                | 1                 | rodman@test.com | rodman@test.com | FILA INCOMPLETA: La primera se ignora (CellsUsed < 3), lee la segunda.|
      |                                                                               | 0                 | N/A             | N/A             | ARCHIVO VACÍO: Solo tiene las 4 filas de cabecera. Retorna 0.         |