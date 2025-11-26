Feature: Filtrado de correos en LeerCorreosDesdeTxt
  Como servicio de correo masivo
  Quiero filtrar correos válidos desde un .txt temporal
  Para asegurar que solo se procesen destinatarios correctos

  Scenario Outline: Normalización y filtrado por regex
    # CHANGE 1: Explicitly state that ";" represents a new line in the step definition
    Given que genero un archivo .txt temporal separando por ";" las líneas "<contenido_txt>"
    
    And uso el regex "^[^@\s]+@[^@\s]+\.[^@\s]+$" para validar cada línea
    When ejecuto la función de filtrado LeerCorreosDesdeTxt
    Then la lista resultante debe ser "<lista_valida>"
    And la aserción Passed debe ser True

    Examples: Casos de prueba
      | contenido_txt                                                 | lista_valida                                               | Notas                                                                 |
      | simple@test.com;admin@empresa.org; allunav@utn.edu.ec         | admin@empresa.org,allunav@utn.edu.ec,simple@test.com       | Caso base: Todos validos. Removemos el espacio erróneo en simple@.    |
      |  espacios@inicio.com ; final@espacios.com                     | espacios@inicio.com,final@espacios.com                     | Trim(): El código C# hace .Trim() por línea, eliminando espacios ext. |
      | usuario@dominio; @solodominio.com; user@.com                  |                                                            | Inválidos: El regex requiere caracteres antes Y después del punto.    |
      | sinespacio@test.com; user@dom inio.com                       | sinespacio@test.com                                       | Inválidos: El regex [^@\s] prohíbe espacios internos.                 |
      | user+tag@gmail.com; nombre.apellido@sub.domain.co.uk          | user+tag@gmail.com,nombre.apellido@sub.domain.co.uk        | Válidos: El regex permite + y subdominios múltiples.                  |
      | ;   ;                                                         |                                                            | Vacíos: IsNullOrEmpty ignora líneas vacías tras el split.             |
      | MIXTO@test.com; malo-sin-arroba;  valido.trim@test.net  ;user@local | MIXTO@test.com,valido.trim@test.net                  | Mixto: user@local falla porque el regex obliga a tener un punto (.).  |